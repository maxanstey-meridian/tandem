# Tandem

Tandem is a typed .NET SDK for building agentic applications as explicit pipelines.
Agents, ordinary C# stages, and human interactions share immutable application state;
routes define what happens next.

```text
agent -> C# stage -> human -> agent
  |                              |
  +----------- explicit loop ---+
```

The configured pipeline is the lifecycle. There is no hidden application-level
coordinator deciding which participant runs next.

## What It Is

- A small authoring model for typed, in-process agent workflows.
- Explicit composition: every transition is a named route.
- State-first: agents and stages read and update your `TState`.
- Typed at machine boundaries: model output is deserialized, validated, then applied.
- Built on Microsoft Agent Framework for model loops, sessions, tools, and execution.

## What It Is Not

- A durable workflow service, scheduler, daemon, or distributed queue.
- A hidden multi-agent coordinator or prompt-driven routing framework.
- A replacement for ordinary application code: deterministic work remains C#.
- A security sandbox for tools or commands.

Runs belong to the process that starts them. Cancellation remains cancellation;
undeclared faults remain exceptions.

## The Basic Shape

This abbreviated review loop contains the core Tandem concepts:

```csharp
// CodingState carries the task, the proposed change, and the latest review decision.
public sealed record CodingState(
    string Instructions,
    string? ProposedChange = null,
    bool Approved = false,
    string? ReviewNotes = null);

// The coder turns the instructions and any review notes into a proposed change.
var coder = Agent
    .Create<CodingState>(
        "coder",
        "You are a coding agent. Make the requested change and respond to review notes.",
        coderClient)
    // Each pass includes the original task, current work, and latest review notes.
    .WithMessage(state =>
        $"Instructions: {state.Instructions}\n"
        + $"Current change: {state.ProposedChange}\n"
        + $"Review notes: {state.ReviewNotes}")
    // Require the agent to return a CodingDecision.
    // Validate that decision, then use ApplyDecision to update CodingState.
    .WithOutput(new CodingDecisionValidator(), CodingPolicies.ApplyDecision)
    .Build();

// Human review pauses the pipeline and returns an approval decision with optional notes.
var humanReview = PipelineNodes.WaitFor<CodingState, ChangeReview, ReviewAnswer>(
    "human-review",
    // The reviewer receives the proposed change.
    state => new ChangeReview(state.ProposedChange!),
    // Their answer updates the facts used by the outgoing routes.
    (state, answer) => state with
    {
        Approved = answer.Approved,
        ReviewNotes = answer.Notes,
    });

// Deterministic checks remain ordinary C#; Complete marks the successful terminal.
var checks = new CodeCheckStage();
var complete = PipelineNodes.Complete<CodingState>("complete");

// The pipeline starts with the coder and declares every possible transition.
var pipeline = Pipeline
    .Start(coder, "coding-task")
    // Proposed changes pass deterministic checks before reaching a reviewer.
    .Route(coder.Success, checks, "change proposed")
    .Route(checks, humanReview, "checks passed")
    // Approval completes the pipeline.
    .Route(
        from: humanReview,
        when: state => state.Approved,
        to: complete,
        label: "approved")
    // Rejection returns to the coder with ReviewNotes populated.
    .Route(
        from: humanReview,
        when: state => !state.Approved,
        to: coder,
        label: "changes requested")
    .Build(complete);
```

The model produces a typed `CodingDecision`. Tandem validates it before
`CodingPolicies.ApplyDecision` can update state. The human interaction suspends the
run without inventing a service call, and the final two routes make the loop visible.
`CodingDecisionValidator`, `CodingPolicies`, and the review request/answer records are
ordinary application code; the runnable samples show their complete definitions.

Deterministic work uses an ordinary generated stage:

```csharp
// PipelineStage generates the typed adapter used by composition.
[PipelineStage("code-checks")]
public sealed partial class CodeCheckStage
{
    // The stage receives and returns application state directly.
    public ValueTask<CodingState> ExecuteAsync(
        CodingState state,
        CancellationToken cancellationToken)
    {
        // Formatting, compilation, or tests would update the relevant facts here.
        return ValueTask.FromResult(state);
    }
}
```

## Quick Start

Tandem currently targets .NET 10. Reference the core package and its source generator:

```xml
<!-- Core authoring API and in-process runner. -->
<PackageReference Include="Tandem" Version="..." />
<!-- Generates typed adapters for [PipelineStage] classes. -->
<PackageReference Include="Tandem.Generators" Version="..." PrivateAssets="all" />
```

1. Define an immutable state record containing application facts.
2. Create agents with `Agent.Create<TState>()`, `.WithMessage(...)`, and typed
   `.WithOutput(...)`.
3. Keep deterministic operations in ordinary `[PipelineStage]` classes.
4. Model external decisions with `PipelineNodes.WaitFor<TState, TRequest, TResponse>()`.
5. Compose the lifecycle with explicit `.Route(...)` calls.
6. Run it with a typed interaction handler:

```csharp
// This handler connects human-review to the host's UI, CLI, chat, or API.
var handlers = new PipelineInteractionHandlers()
    .Handle(humanReview, AskReviewerAsync);

// The runner executes the pipeline from an initial CodingState.
var result = await new PipelineRunner().RunAsync(
    pipeline,
    // Instructions are the only application fact required at startup.
    new CodingState("Add a friendly greeting to the home page."),
    // Interaction handlers are supplied by the host for this run.
    new PipelineRunOptions(Interactions: handlers),
    cancellationToken);

// The result contains both the terminal status and final typed state.
Console.WriteLine(result.Status); // Succeeded or Failed
Console.WriteLine(result.State.ProposedChange);
```

`Tandem.Advanced` is an explicit opt-in for execution-aware concerns such as
Harness workspaces, tool authority, output acceptance, checkpoints, and custom
operation observations. Most application pipelines should begin with Core only.

## Samples

- [Songwriter](samples/Tandem.Sample.Songwriter): the smallest complete loop;
  typed agents, an ordinary lint stage, and state-owned routing.
- [Support](samples/Tandem.Sample.Support): deterministic I/O plus a typed live
  customer handoff.
- [Debate](samples/Tandem.Sample.Debate): revision loops, retained sessions, and
  typed capabilities.
- [Delivery](src/Tandem.Delivery): an experimental first-party pipeline exercising
  Advanced workspace and verification features.

For the complete API journey, see
[Pipeline Authoring](docs/pipeline-authoring.md). Architecture and contribution
rules live in [CONTRIBUTING.md](CONTRIBUTING.md).

## Repository

- `src/Tandem`: core authoring API and in-process execution.
- `src/Tandem.Advanced`: explicit execution-aware extensions.
- `src/Tandem.Generators`: source-generated stage adapters.
- `samples`: progressively richer runnable examples.
- `tests`: boundary, package, and real in-process execution proofs.

Run the repository checks with:

```sh
dotnet tool restore
task check
```
