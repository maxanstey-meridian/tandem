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

Every Tandem pipeline graph has:

1. A state record containing the facts that must survive between steps.
2. Small steps that return named results.
3. A composition connecting those results to the next steps.

That is the whole graph model. Agent-backed steps add one explicit piece: the
model operation they call when the graph reaches them.

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

// Saved runs use this ID to find their place again after a restart.
[PipelineStage(CoderAgent.StepId)]
public sealed partial class CoderAgent(AgentOperation<CodingState> operation)
{
    public const string StepId = "coder";

    // These are all the ways the coder can hand control back to the pipeline.
    [Union]
    public partial record CoderResult
    {
        // The chosen result carries the state that the next step will receive.
        public partial record Implemented(
            CodingState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome);

        public partial record Unexpected(
            CodingState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome);
    }

    // Tandem calls this when execution reaches "coder", passing in the latest state.
    public async ValueTask<CoderResult> ExecuteAsync(
        PipelineMessage<CodingState> pipeline,
        CancellationToken cancellationToken)
    {
        // AgentOperation owns the model loop, tools, session and durable bookkeeping.
        var result = await operation.RunAsync(pipeline, cancellationToken);

        // This pipeline, not Tandem, decides what the agent's semantic outcome means.
        return result.LatestOutcome?.Kind == "coding.implemented"
            ? new CoderResult.Implemented(
                result.State,
                result.Runtime,
                result.LatestOutcome)
            : new CoderResult.Unexpected(
                result.State,
                result.Runtime,
                result.LatestOutcome!);
    }
}

[PipelineStage(ReviewerAgent.StepId)]
public sealed partial class ReviewerAgent(AgentOperation<CodingState> operation)
{
    public const string StepId = "reviewer";

    // The reviewer has two possible exits, so composition can route them differently.
    [Union]
    public partial record ReviewerResult
    {
        public partial record Accepted(
            CodingState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome);

        public partial record ChangesRequested(
            CodingState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome);
    }

    // This receives the exact state returned by the coder, including its implementation.
    public async ValueTask<ReviewerResult> ExecuteAsync(
        PipelineMessage<CodingState> pipeline,
        CancellationToken cancellationToken)
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);

        // The structured-output policy has already validated and applied the findings.
        return result.LatestOutcome?.Kind == "review.accepted"
            ? new ReviewerResult.Accepted(
                result.State,
                result.Runtime,
                result.LatestOutcome)
            : new ReviewerResult.ChangesRequested(
                result.State,
                result.Runtime,
                result.LatestOutcome!);
    }
}
```

There are two conventions to remember:

- The class is `partial` because Tandem adds its adapter at compile time.
- Every result case starts with a property named `State`. That is the value the
  next step receives.

The generator also remembers which step and result case produced the state. That
information is saved with the run, so routing still works after serialization and
restart.

### 3. Configure the Agents

`[PipelineStage]` makes a class routable; it does not silently call an LLM. The
composition root supplies an `IChatClient` and uses `AgentRuntime` to configure
the model-backed operation explicitly:

```csharp
public sealed record CodingClients(
    IChatClient Coder,
    IChatClient Reviewer);

public sealed record ImplementationDecision(string Implementation);

public sealed record ReviewDecision(
    bool Accepted,
    IReadOnlyList<string> Findings);

public sealed class CodingStepsFactory(
    AgentRuntime agents,
    CodingClients clients)
{
    public CodingSteps Create(PipelineBuildContext context)
    {
        var coder = agents
            .Create<CodingState>(
                // Reuse the step ID so saved agent sessions cannot drift from the graph.
                id: CoderAgent.StepId,
                profile: "coding",
                instructions: CodingPrompts.Coder,
                chatClient: clients.Coder)
            // The prompt is rebuilt from the latest durable state on every visit.
            .WithMessage(pipeline => $"Implement: {pipeline.State.Request}")
            // The parser validates model output and returns the updated CodingState.
            .WithStructuredOutput(
                CodingPolicies.ParseImplementation,
                // Ask the provider for this shape; never trust the shape without parsing it.
                chat => chat.ResponseFormat =
                    ChatResponseFormat.ForJsonSchema<ImplementationDecision>())
            // File tools are absent unless the pipeline deliberately adds a workspace.
            .WithWorkspace(
                path: state => state.Workspace,
                allowMutation: _ => true)
            // Session behavior is product policy, so Tandem never guesses it from "coder".
            .WithSessionPolicy(CodingPolicies.ContinueWorkingSession)
            .Build(context);

        var reviewer = agents
            .Create<CodingState>(
                id: ReviewerAgent.StepId,
                profile: "review",
                instructions: CodingPrompts.Reviewer,
                chatClient: clients.Reviewer)
            .WithMessage(pipeline => $"Review revision {pipeline.State.Revision}.")
            .WithStructuredOutput(
                CodingPolicies.ParseReview,
                chat => chat.ResponseFormat =
                    ChatResponseFormat.ForJsonSchema<ReviewDecision>())
            .WithWorkspace(
                path: state => state.Workspace,
                allowMutation: _ => false)
            .WithSessionPolicy(CodingPolicies.StartFreshReview)
            .Build(context);

        return new CodingSteps(
            new CoderAgent(coder),
            new ReviewerAgent(reviewer),
            new CompleteStage());
    }
}
```

The `IChatClient` is the object that actually talks to the model. Tandem does not
choose a provider behind your back. The `profile` is the stable name recorded in
the run for policy and usage bookkeeping; Delivery also supplies a profile-to-
client function because it can deliberately promote an agent to another model.

`AgentRuntime` itself comes from `services.AddTandem()`. The clients and
pipeline-specific factory come from your composition root:

```csharp
services.AddTandem();

// Create these with whichever Microsoft.Extensions.AI provider your host uses.
services.AddSingleton(new CodingClients(coderClient, reviewerClient));
services.AddSingleton<CodingStepsFactory>();
```

`Build(context)` binds only run-specific observers, such as streamed agent text
or command output. Stable clients and policies stay in DI, while concurrent
pipeline builds keep their callbacks isolated.

`AgentRuntime` keeps MAF, session persistence, retries, streaming, tool dispatch,
and profile bookkeeping behind Tandem's boundary. The authored agent still owns
its prompt, capabilities, state transition, and result vocabulary.

The compiled Support classifier simply omits `.WithWorkspace(...)`; it receives
no filesystem tools and `SupportState` has no workspace property.

### Where the State Change Happens

`.WithStructuredOutput(...)` is not just JSON deserialization. Its parser decides
whether the answer is acceptable, gives the outcome a stable semantic name, and
returns the next durable state:

```csharp
public static StructuredOutputResult<CodingState> ParseImplementation(
    string assistantText,
    PipelineMessage<CodingState> pipeline)
{
    try
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(assistantText);
        var implementation = payload.GetProperty("implementation").GetString();

        if (string.IsNullOrWhiteSpace(implementation))
        {
            return new(
                null,
                [new("implementation", "Implementation must not be blank.")],
                assistantText,
                payload);
        }

        // This snapshot is what ReviewerAgent receives if routing selects it next.
        var state = pipeline.State with
        {
            Implementation = implementation,
            Revision = pipeline.State.Revision + 1
        };

        return new(
            new StructuredOutcome<CodingState>(
                "coding.implemented",
                $"Implemented revision {state.Revision}.",
                payload,
                state),
            [],
            assistantText,
            payload);
    }
    catch (Exception exception)
        when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        // Tandem turns these problems into one corrective model reply, then fails closed.
        return new(
            null,
            [new("$", exception.Message)],
            assistantText);
    }
}
```

That `"coding.implemented"` value is the same semantic outcome inspected by
`CoderAgent.ExecuteAsync`. The parser owns the state transition; the authored
Dunet case owns the route vocabulary. Tandem records both without inventing a
meaning for either.

Session policy is similarly ordinary pipeline code:

```csharp
public static AgentSessionDecision ContinueWorkingSession(
    PipelineMessage<CodingState> pipeline) =>
    pipeline.State.Revision == 0
        ? new(AgentSessionAction.Reset, "Start this request with a clean context.")
        : new(AgentSessionAction.Continue, "Keep context while revising this request.");
```

The reason is persisted alongside the decision, which makes the lifecycle
visible rather than hiding it in role-name conventions.

### 4. Connect the Results

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
    string CustomerId,
    string? Category = null,
    string? AccountContext = null,
    string? ProposedResolution = null,
    string? CustomerReply = null,
    string? FinalDisposition = null);
```

Waiting for a reply is also configured in userland, but persistence is not. The
sample gives Tandem the three IDs and two pure transformations:

```csharp
var customerReply = PipelineNodes.Request<
    SupportState,
    CustomerQuestion,
    CustomerReply
>(
    // These IDs remain visible in inspection and durable execution history.
    requestStepId: SupportIds.AskCustomer,
    portId: SupportIds.CustomerReply,
    resumeStepId: SupportIds.ApplyReply,
    // Userland decides what leaves the process and how the answer changes state.
    createRequest: SupportPolicies.BuildCustomerQuestion,
    applyResponse: SupportPolicies.ApplyCustomerReply);
```

`customerReply.Request`, `.Port`, and `.Resume` are ordinary nodes in the graph.
Between Port and Resume, Tandem stores and restores the complete typed pipeline
message; the support package never handles checkpoint scopes or serialized state.

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
            to: support.CustomerReply.Request,
            label: "resolution proposed")
        // Request builds the question and Tandem saves the current pipeline message.
        .Route(
            from: support.CustomerReply.Request,
            to: support.CustomerReply.Port,
            label: "wait for customer")
        // Port can wait indefinitely without keeping this process alive.
        .Route(
            from: support.CustomerReply.Port,
            to: support.CustomerReply.Resume,
            label: "customer replied")
        // Resume restores the same PipelineMessage and applies the typed CustomerReply.
        .Route(
            when: message => message.State.FinalDisposition == "closed",
            from: support.CustomerReply.Resume,
            to: support.Close,
            label: "customer confirmed")
        // A blocked customer leaves automation through an explicit escalation route.
        .Route(
            when: message => message.State.FinalDisposition == "escalated",
            from: support.CustomerReply.Resume,
            to: support.Escalate,
            label: "human help needed")
        // Both closure and escalation are valid outcomes of this workflow.
        .Build(support.Close, support.Escalate);
```

This is compiled sample code, not a hypothetical graph. `Classify` and `Resolve`
use explicitly configured `AgentOperation<SupportState>` instances. `LoadAccount`
uses an injected deterministic `IAccountLookup`. `CustomerReply` is a typed
`PipelineRequest<SupportState, CustomerQuestion, CustomerReply>` that owns durable
save and restoration, so the sample contains no execution-context, JSON-storage,
or framework plumbing. `Escalate` receives all context already collected.

See [`samples/Tandem.Sample.Support`](samples/Tandem.Sample.Support) for the full
prompts, policies, registration, state transitions, and composition. Its tests
execute both terminal paths, suspend and resume through the real request port,
and run the closed-generic pipeline through Durable Task Scheduler.

The same approach fits research synthesis, incident response, editorial review,
data-quality remediation, approval workflows, and other processes where the next
action depends on a typed result.

## What the Source Generator Adds

Reference `Tandem.Generators` as an analyzer and mark each step with a stable ID,
normally shared with agent configuration through one constant, as `CoderAgent`
above and the compiled Support agents do. At compile time, Tandem adds:

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
private route around Tandem's public API. Its composition contains only the graph;
Delivery-owned prompts, policies, and Git capabilities are assembled through the
same public SDK and DI boundaries available to another pipeline package.

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
- `samples/Tandem.Sample.Support`: customer-support pipeline with durable reply handoff
- `samples/Tandem.Sample.Debate`: independent external-consumer example
- `tests/Tandem.Tests`: unit, architecture, in-process, and durable tests

Run all checks with:

```sh
dotnet tool restore
task check
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the architectural boundaries.
