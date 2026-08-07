# Tandem

Tandem is a .NET library for building durable agentic pipelines that can run for
minutes, hours, or days and continue after the host process restarts.

You write ordinary C# classes for work and compose their successors explicitly.
Tandem builds that graph as a Microsoft Agent Framework workflow. MAF owns
orchestration, durability, agent loops, sessions, and tool dispatch; authored
pipeline code owns state, prompts, policies, capabilities, meaningful results,
and routes.

```text
write -> lint -> review -> complete
  ^         |       |
  +---------+-------+
```

The configured graph is the lifecycle. Fluent call order never creates an
implicit successor: every edge is declared with `Route`.

## Start With Songwriter

[`samples/Tandem.Sample.Songwriter`](samples/Tandem.Sample.Songwriter) is the
smallest complete authoring example. Its durable state is an immutable record,
and its steps demonstrate three of the four inferred `ExecuteAsync` forms:

```csharp
[PipelineStage(SongwriterAgent.StepId)]
public sealed partial class SongwriterAgent(AgentOperation<SongwriterState> operation)
{
    // IDs are durable workflow identity, so the step and operation share one constant.
    public const string StepId = "songwriter";

    public async ValueTask<Outcome<SongwriterState>> ExecuteAsync(
        SongwriterState state,
        CancellationToken cancellationToken
    ) => await operation.RunAsync(state, cancellationToken);
}
```

Returning `Outcome<TState>` exposes Tandem's standard `Success` and `Failed`
selectors. Songwriter follows only successful model execution; an unhandled
failure ends the run as failed:

```csharp
.Route(on: song.Songwriter.Result.Success, to: song.Lint, label: "song written")
```

The terminal step is even smaller:

```csharp
[PipelineStage(CompleteSongStage.StepId)]
public sealed partial class CompleteSongStage
{
    public const string StepId = "complete";

    public ValueTask ExecuteAsync(SongwriterState _, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
```

Returning `ValueTask` preserves the current state and produces standard success.
It also has no result selectors.

Use a custom Dunet union only when the graph has semantic branches worth naming:

```csharp
[PipelineStage(ProofreaderAgent.StepId)]
public sealed partial class ProofreaderAgent(AgentOperation<SongwriterState> operation)
{
    public const string StepId = "proofreader";

    [Union(EnableImplicitConversions = false)]
    public partial record ProofreaderResult
    {
        public partial record Accepted(SongwriterState State);
        public partial record ChangesRequested(SongwriterState State);
        public partial record Failed(SongwriterState State, FailureEvidence Failure);
    }

    public async ValueTask<ProofreaderResult> ExecuteAsync(
        SongwriterState state,
        CancellationToken cancellationToken
    ) =>
        await operation.RunAsync<ProofreaderResult>(
            state,
            result =>
                result.Outcome.Kind == SongwriterPolicies.ProofAcceptedOutcome
                    ? new ProofreaderResult.Accepted(result.State)
                    : new ProofreaderResult.ChangesRequested(result.State),
            // Model/runtime failure is not a request to revise valid proofreader feedback.
            failure => new ProofreaderResult.Failed(state, failure),
            cancellationToken
        );
}
```

Those cases generate only the corresponding typed selectors:

```csharp
.Route(on: song.Proofreader.Result.Accepted, to: song.Complete, label: "proof accepted")
.Route(
    on: song.Proofreader.Result.ChangesRequested,
    to: song.Songwriter,
    label: "changes requested"
)
.Route(on: song.Proofreader.Result.Failed, to: song.Failed, label: "agent failed")
```

`song.Failed` is created with `PipelineNodes.Failed<SongwriterState>(...)`. It
preserves the failure evidence and terminates with Tandem's failed disposition.

## Four Inferred Step Forms

The source generator infers a step's authoring mode from its `ExecuteAsync`
signature. There is no mode setting and no universal result-union requirement.

```csharp
ValueTask ExecuteAsync(TState state, CancellationToken cancellationToken)
```

Pass-through: preserves state, produces standard success, and exposes no
`.Result` selectors.

```csharp
ValueTask<TState> ExecuteAsync(TState state, CancellationToken cancellationToken)
```

State-updating: uses the returned state, produces standard success, and exposes
no `.Result` selectors.

```csharp
ValueTask<Outcome<TState>> ExecuteAsync(
    TState state,
    CancellationToken cancellationToken
)
```

Standard outcome: returns typed `Success` or `Failed` and exposes exactly those
selectors. The compiled core contract is:

```csharp
public abstract record Outcome<TState>
{
    private Outcome() { }

    public sealed record Success(TState State) : Outcome<TState>;
    public sealed record Failed(TState State, FailureEvidence Failure) : Outcome<TState>;
}

public sealed record FailureEvidence(string Code, string Summary, string? Detail = null);
```

```csharp
ValueTask<TCustomResult> ExecuteAsync(
    TState state,
    CancellationToken cancellationToken
)
```

Custom result: `TCustomResult` is a nested Dunet union and exposes only its
declared cases. Use this form for branching vocabulary such as `Accepted`,
`ChangesRequested`, or `NeedsHuman`, not for routine success.

A standard `Failed` result is recoverable pipeline data only when an unconditional
route handles it or at least one conditional route matches its failed state. An
unhandled `Failed` ends the run as failed. Exceptions are undeclared faults, and
cancellation remains cancellation; neither follows an ordinary output route.

## State-First Agents And Policies

Agent construction is explicit and scoped to each pipeline build. Songwriter's
compiled factory uses state-first callbacks throughout:

```csharp
agents
    .Create<SongwriterState>(id, id, instructions, client)
    .WithMessage(state =>
        $"Brief: {state.Brief}\nLyrics: {state.Lyrics}\n"
        + $"Lint: {state.LintFeedback}\nProofreader: {state.ProofreaderFeedback}"
    )
    .WithStructuredOutput(parser, configureChatOptions)
    .WithSessionPolicy(SongwriterPolicies.StartFresh)
    .Build(context);
```

`WithMessage` receives `TState`. Structured-output parsers receive the assistant
text and `TState`. Session, profile, workspace, and ordinary route predicates are
also state-first. Tandem transports run identity, sessions, usage, invocation
counts, profiles, outcomes, routing identity, and replay metadata internally.
Ordinary user code does not copy an execution envelope.

An agent can return Tandem's standard outcome directly:

```csharp
return await operation.RunAsync(state, cancellationToken);
```

Or it can map legitimate operation evidence into a custom semantic result, as the
Songwriter proofreader does above. `OperationResult<TState>` exposes only `State`
and `OperationOutcome`; it does not expose runtime bookkeeping.

## Explicit Routing

Use an unconditional output route for serial flow when every produced result is
allowed to continue:

```csharp
.Route(on: support.LoadAccount, to: support.Resolve, label: "account loaded")
```

Use a result-specific route only when a standard or custom case controls the
successor:

```csharp
.Route(on: song.Lint.Result.Passed, to: song.Proofreader, label: "lint passed")
.Route(on: song.Lint.Result.Failed, to: song.Songwriter, label: "lint failed")
.Route(on: song.Songwriter.Result.Success, to: song.Lint, label: "song written")
```

Normal predicates receive state:

```csharp
.Route(
    when: state => state.FinalDisposition == "closed",
    from: support.CustomerReply.Resume,
    to: support.Close,
    label: "customer confirmed"
)
```

Do not mix unconditional and result-specific routes from one source: both would
match the same output. Tandem rejects that accidental fan-out. Use
`RouteWithContext` only when an advanced route genuinely requires the complete
execution message.

Raw request ports and advanced envelope-aware nodes implement `IRawPipelineNode`.
Generated steps do not, so an untyped route cannot become a fallback that wires
generated steps with incompatible state types.

## Progressive Samples

The public SDK is one progressive journey rather than separate basic and advanced
programming models:

- **Songwriter** proves pass-through and state-updating steps, semantic Dunet
  branches, unconditional serial routes, and agent execution without workspace or
  runtime plumbing.
- **Support** adds consumer-owned account lookup, typed state transitions, and a
  durable typed customer request/response handoff.
- **Debate** adds revision loops, explicit retained/reset sessions, a lifecycle
  action with a receipt-backed state transition, and teardown based on block
  evidence.
- **Delivery** adds custom blocks, workspaces and mutation policy, checkpoints,
  tools, verification commands, observations, and durable human handoff.

### Durable Support Handoff

Support constructs its request nodes with state-first transformations:

```csharp
var customerReply = PipelineNodes.Request<SupportState, CustomerQuestion, CustomerReply>(
    SupportIds.AskCustomer,
    SupportIds.CustomerReply,
    SupportIds.ApplyReply,
    SupportPolicies.BuildCustomerQuestion,
    SupportPolicies.ApplyCustomerReply
);
```

The returned `PipelineRequest<SupportState, CustomerQuestion, CustomerReply>`
exposes `Request`, `Port`, and `Resume`. Tandem saves and restores the complete
execution envelope around the port; Support only creates the typed request and
applies the typed response to `SupportState`.

### Debate Sessions And Teardown

Debate keeps ordinary session policy state-first:

```csharp
public static AgentSessionDecision RetainRevisionContext(DebateState _) =>
    new(AgentSessionAction.Continue, "Retain critic context across revision rounds.");
```

Its judge teardown policy intentionally uses advanced block context:

```csharp
public static AgentTeardownDecision ReleaseJudgeAfterVerdict(
    PipelineMessage<DebateState> _,
    BlockOutcome __
) => new(true, true, "Release judge bookkeeping after an accepted verdict.");
```

## Advanced Blocks

`PipelineMessage<TState>` and `BlockOutcome` are supported Tandem SDK concepts for
custom blocks and runtime policies that genuinely need execution context. They
are not the ordinary step contract.

```csharp
public sealed record PipelineMessage<TState>(
    PipelineRuntime Runtime,
    TState State,
    BlockOutcome? LatestOutcome = null,
    PipelineResult? LatestResult = null,
    PipelineRunDisposition? Disposition = null
);

public sealed record BlockOutcome(
    string Kind,
    string BlockId,
    string Summary,
    JsonElement Payload = default,
    TimeSpan Duration = default
);
```

Delivery uses these APIs in custom workspace, candidate-capture, verification,
terminal, and human-input blocks. Generated adapters preserve the active envelope
when ordinary steps return only state or a semantic result. Advanced block code
may use `PipelineOperation.RunAsync` to adapt a block execution into an authored
custom result without manually copying runtime fields.

## Inspect And Run

`Inspect()` describes the exact built workflow, including start and output nodes,
routes, conditions, request ports, and Mermaid/DOT diagrams:

```csharp
var inspection = composition.Build(new PipelineBuildContext()).Inspect();
Console.WriteLine(inspection.Mermaid);
Console.WriteLine(inspection.Dot);
```

Run the included Delivery packet:

```sh
dotnet run --project src/Tandem.Tool -- run examples/01-todo-api/packet.md
```

Reconnect or publish a ready candidate:

```sh
dotnet run --project src/Tandem.Tool -- attach <run-id>
dotnet run --project src/Tandem.Tool -- publish <run-id> --branch tandem/my-change
```

The Delivery host requires .NET 10, Docker, a Durable Task Scheduler, and an
OpenAI-compatible model provider.

## Repository

- `src/Tandem`: authoring API and execution engine
- `src/Tandem.Generators`: incremental source generator
- `src/Tandem.Delivery`: advanced first-party acceptance consumer
- `src/Tandem.Tool`: `run`, `attach`, and `publish` host
- `samples/Tandem.Sample.Songwriter`: minimal progressive example
- `samples/Tandem.Sample.Support`: durable customer-support handoff
- `samples/Tandem.Sample.Debate`: loops, lifecycle actions, and teardown policy
- `tests/Tandem.Tests`: unit, architecture, in-process, and durable tests

See [Pipeline Authoring](docs/pipeline-authoring.md) for the complete API journey
and [CONTRIBUTING.md](CONTRIBUTING.md) for architecture rules.

Run all checks with:

```sh
dotnet tool restore
task check
```
