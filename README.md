# Tandem

Tandem is a .NET library for building agentic pipelines that can run for minutes,
hours, or days and carry on after your process restarts.

You write ordinary C# classes for the work. One class might call an agent,
another might run a command, and another might wait for a person. Each class says
how it finished, and your pipeline says what should happen next.

```text
write code -> review it -> accepted -> finish
                    |
                    +-> changes requested -> write code again
```

There is no coordinator hidden behind that diagram. The diagram *is* the
coordinator.

Tandem builds the diagram as a real Microsoft Agent Framework workflow. MAF does
the hard runtime work: scheduling steps, saving progress, restoring runs, keeping
agent sessions alive, and dispatching tools. Your code stays concerned with your
pipeline.

## The Three Things You Write

Every Tandem pipeline has:

1. A state record containing the facts that must survive between steps.
2. Small steps that return named results.
3. A composition connecting those results to the next steps.

That is the whole programming model.

## A Small Coder-Reviewer Pipeline

Suppose we want one agent to implement a request and another to review it. If the
reviewer finds a problem, the work goes back to the coder. Otherwise, the run
finishes.

### 1. Decide What Must Survive

The state is the pipeline's memory. Put values here when a later step will need
them, or when they must still exist after a restart. Do not put clients, services,
or framework objects here; those come from dependency injection when a step runs.

```csharp
public sealed record CodingState(
    string Request,
    string Workspace,
    string? Implementation = null,
    IReadOnlyList<string>? Findings = null,
    int Revision = 0
);
```

Tandem carries this value from step to step inside a
`PipelineMessage<CodingState>`. Because the state is immutable, a step returns a
new version with the facts it learned.

### 2. Write the Steps

A step has one `ExecuteAsync` method and a small result union. The result cases
are important: they become the choices available when we connect the pipeline.

```csharp
using Dunet;
using Tandem;
using Tandem.Domain;

// This ID is written into saved runs. Keep it stable if old runs must still resume.
[PipelineStage("coder")]
public sealed partial class CoderAgent
{
    // These are all the ways the coder can hand control back to the pipeline.
    [Union]
    public partial record CoderResult
    {
        // The chosen result carries the state that the next step will receive.
        public partial record Implemented(CodingState State);
    }

    // Tandem calls this when execution reaches "coder", passing in the latest state.
    public async ValueTask<CoderResult> ExecuteAsync(
        PipelineMessage<CodingState> pipeline,
        CancellationToken cancellationToken)
    {
        // The work itself can be an agent call, a normal service, or any async operation.
        var implementation = await WriteCodeAsync(
            pipeline.State.Request,
            pipeline.State.Workspace,
            cancellationToken);

        // Return a new snapshot; Tandem saves it before following the Implemented route.
        return new CoderResult.Implemented(
            pipeline.State with
            {
                Implementation = implementation,
                Revision = pipeline.State.Revision + 1
            });
    }
}

[PipelineStage("reviewer")]
public sealed partial class ReviewerAgent
{
    // The reviewer has two possible exits, so composition can route them differently.
    [Union]
    public partial record ReviewerResult
    {
        public partial record Accepted(CodingState State);
        public partial record ChangesRequested(CodingState State);
    }

    // This receives the exact state returned by the coder, including its implementation.
    public async ValueTask<ReviewerResult> ExecuteAsync(
        PipelineMessage<CodingState> pipeline,
        CancellationToken cancellationToken)
    {
        var findings = await ReviewAsync(pipeline.State, cancellationToken);

        // Keep the evidence in durable state so the coder can see it on another pass.
        var state = pipeline.State with { Findings = findings };

        // Returning a case chooses a named route; no string comparison is needed here.
        return findings.Count == 0
            ? new ReviewerResult.Accepted(state)
            : new ReviewerResult.ChangesRequested(state);
    }
}
```

The calls to `WriteCodeAsync` and `ReviewAsync` are placeholders for whichever
agents or operations you register. The Tandem-facing parts are complete.

There are two conventions to remember:

- The class is `partial` because Tandem adds its adapter at compile time.
- Every result case starts with a property named `State`. That is the value the
  next step receives.

The generator also remembers which step and result case produced the state. That
information is saved with the run, so routing still works after serialization and
restart.

### 3. Connect the Results

Put the step instances in a small record, then describe the graph. The generated
`Result` property is why the routes below are strongly typed rather than strings.

```csharp
public sealed record CodingSteps(
    CoderAgent Coder,
    ReviewerAgent Reviewer,
    CompleteStage Complete);

public sealed class CodingComposition(CodingSteps coding)
{
    public Pipeline Build() =>
        TandemWorkflow
            // Every run enters through one explicit step.
            .Start(
                at: coding.Coder,
                name: "coding",
                description: "Implement and review a requested change.")
            // Result.Implemented is generated from the CoderResult case above.
            .Route(
                on: coding.Coder.Result.Implemented,
                to: coding.Reviewer,
                label: "implementation ready")
            // Pointing back to Coder creates the review loop directly in the graph.
            .Route(
                on: coding.Reviewer.Result.ChangesRequested,
                to: coding.Coder,
                label: "changes requested")
            // Accepted leaves the loop and enters the final step.
            .Route(
                on: coding.Reviewer.Result.Accepted,
                to: coding.Complete,
                label: "accepted")
            // Declaring Complete as output tells the runtime where a successful run ends.
            .Build(coding.Complete);
}
```

Read it from top to bottom:

- Start with the coder.
- When the coder returns `Implemented`, run the reviewer.
- When the reviewer returns `ChangesRequested`, go around the loop.
- When the reviewer returns `Accepted`, run the final step.

Calling `.Route(...)` adds the real workflow edge immediately. Tandem does not
keep a second, slightly different copy of your graph behind the scenes.

Sometimes a result needs more than one possible destination. Add a state
predicate for that case:

```csharp
// This route is considered only after the reviewer returns ChangesRequested.
.Route(
    on: coding.Reviewer.Result.ChangesRequested,
    // The same result can lead elsewhere once the retry budget is exhausted.
    when: message => message.State.Revision < 3,
    to: coding.Coder,
    label: "revise again")
```

The result says *what happened*. The predicate decides whether this particular
state should take that route.

## What Tandem Carries Between Steps

Most pipeline code only uses `pipeline.State`, but the complete message looks
like this:

```csharp
public sealed record PipelineMessage<TState>(
    PipelineRuntime Runtime,
    TState State,
    BlockOutcome? LatestOutcome = null,
    PipelineResult? LatestResult = null);
```

- `State` is your pipeline's durable state.
- `Runtime` tracks the run, agent sessions, token usage, and invocation numbers.
- `LatestOutcome` carries optional evidence from an operation or agent action.
- `LatestResult` records the step ID, result case, and serialized result payload.

You update `State`. Tandem preserves and updates the other pieces as execution
moves through the graph.

## It Is Not Just a Coding Harness

Coder-reviewer is an obvious agent loop, but there is nothing about code review
inside Tandem. Here is the same programming model handling a customer-support
ticket.

The durable state holds the original ticket and the context gathered while trying
to resolve it:

```csharp
public sealed record SupportState(
    string Ticket,
    string? Category = null,
    string? CustomerContext = null,
    string? ProposedResolution = null,
    string? CustomerReply = null);
```

The pipeline classifies the ticket, loads account context, proposes a resolution,
waits for the customer, and either closes the ticket or hands it to a person:

```csharp
public Pipeline Build() =>
    TandemWorkflow
        // Classification can use an agent without giving it access to account systems.
        .Start(at: support.Classify, name: "customer-support")
        // Account lookup is a normal deterministic step in the same pipeline.
        .Route(
            on: support.Classify.Result.Categorized,
            to: support.LoadAccount,
            label: "issue classified")
        // The resolver receives both the ticket and the context added by LoadAccount.
        .Route(
            on: support.LoadAccount.Result.Loaded,
            to: support.Resolve,
            label: "account context loaded")
        // A proposed answer is sent to the customer rather than treated as success.
        .Route(
            on: support.Resolve.Result.ResolutionProposed,
            to: support.AskCustomer,
            label: "resolution proposed")
        // CustomerReply is a request port: the durable run can wait here indefinitely.
        .Route(
            from: support.AskCustomer,
            to: support.CustomerReply,
            label: "wait for customer")
        // Execution resumes with the same SupportState when a reply arrives.
        .Route(
            from: support.CustomerReply,
            to: support.ApplyReply,
            label: "customer replied")
        // Confirmation closes the ticket without involving a human operator.
        .Route(
            on: support.ApplyReply.Result.Resolved,
            to: support.Close,
            label: "customer confirmed")
        // A blocked customer leaves automation through an explicit escalation route.
        .Route(
            on: support.ApplyReply.Result.StillBlocked,
            to: support.Escalate,
            label: "human help needed")
        // Both closure and escalation are valid outcomes of this workflow.
        .Build(support.Close, support.Escalate);
```

`Classify` and `Resolve` might use agents. `LoadAccount` might call a customer
database. `CustomerReply` can suspend the run for hours or days without keeping a
process alive. `Escalate` can create a case for a human with all the context
already collected. They compose in one graph because Tandem cares about steps and
results, not the subject of the work.

The same approach fits research synthesis, incident response, editorial review,
data-quality remediation, approval workflows, and other processes where the next
action depends on a typed result.

## What the Source Generator Adds

Reference `Tandem.Generators` as an analyzer and mark each step with
`[PipelineStage("stable-id")]`. At compile time, Tandem adds:

- the adapter that lets MAF execute the class;
- the `.Result.<Case>` properties used by composition;
- the code that carries state and runtime information forward; and
- the durable step ID, case ID, and result payload.

This is generated glue, not generated business logic. Your `ExecuteAsync` method
and result cases remain the source of truth. Invalid step shapes fail the build
with `TANDEM001` or `TANDEM002` instead of failing during a run.

See [Pipeline Authoring](docs/pipeline-authoring.md) for the complete authoring
journey and project setup.

## Delivery

`Tandem.Delivery` is the included software-delivery pipeline. It uses the same API
shown above to:

- prepare an isolated Git workspace;
- ask a planner before allowing mutation;
- run a sessioned implementation agent;
- execute packet-defined verification commands;
- review the exact candidate that was verified;
- wait for human input and resume after a restart; and
- publish an accepted candidate as a local branch.

Delivery is an ordinary Tandem consumer. It has no direct MAF dependency and no
private route around Tandem's public API.

Run the included example packet:

```sh
dotnet run --project src/Tandem.Tool -- run examples/01-todo-api/packet.md
```

Reconnect to a durable run or publish a ready candidate:

```sh
dotnet run --project src/Tandem.Tool -- attach <run-id>
dotnet run --project src/Tandem.Tool -- publish <run-id> --branch tandem/my-change
```

The Delivery host requires .NET 10, Docker, a Durable Task Scheduler, and an
OpenAI-compatible model provider.

## Inspect a Pipeline

`Inspect()` describes the exact workflow that will run:

```csharp
// Build first, then inspect that exact executable graph.
var inspection = composition.Build().Inspect();

// Mermaid and DOT are ready to render; no separate diagram definition is required.
Console.WriteLine(inspection.Name);
Console.WriteLine(inspection.Mermaid);
Console.WriteLine(inspection.Dot);
```

It includes the start step, all step IDs, routes, conditions, request ports, final
steps, and Mermaid/DOT diagrams. The topology and diagrams come from the built MAF
workflow, not a separate Tandem diagram model.

## Repository

- `src/Tandem`: authoring API and execution engine
- `src/Tandem.Generators`: incremental source generator
- `src/Tandem.Delivery`: flagship software-delivery pipeline
- `src/Tandem.Tool`: `run`, `attach`, and `publish` host
- `samples/Tandem.Sample.Debate`: independent external-consumer example
- `tests/Tandem.Tests`: unit, architecture, in-process, and durable tests

Run all checks with:

```sh
dotnet tool restore
task check
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the architectural boundaries.
