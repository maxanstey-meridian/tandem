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
smallest complete authoring example. Its durable state is an immutable record.
A default declarative agent is already a typed pipeline step:

```csharp
var songwriter = agents
    .Create<SongwriterState>(
        "songwriter",
        "songwriter",
        "Write or revise lyrics from the brief and current feedback.",
        clients.Songwriter
    )
    .WithMessage(state =>
        $"Brief: {state.Brief}\nLyrics: {state.Lyrics}\n"
        + $"Lint: {state.LintFeedback}\nProofreader: {state.ProofreaderFeedback}"
    )
    .WithOutput(new SongDecisionValidator(), SongwriterPolicies.ApplySong)
    .WithSessionPolicy(SongwriterPolicies.StartFresh)
    .Build();
```

`AgentDefinition<TState>` owns its ID, state type, standard `Outcome<TState>`
execution, and typed `Success`/`Failed` selectors. No forwarding
`[PipelineStage]` class is required. Songwriter follows only successful model
execution; an unhandled failure ends the run as failed:

```csharp
.Route(on: song.Songwriter.Success, to: song.Lint, label: "song written")
```

Successful terminals are SDK nodes rather than empty authored classes:

```csharp
var complete = PipelineNodes.Complete<SongwriterState>("complete");
```

The terminal preserves current state and produces standard success.

Put semantic branch facts in durable state and route successful execution with a
state predicate:

```csharp
.Route(
    on: song.Proofreader.Success,
    when: state => state.ProofreaderAccepted,
    to: song.Complete,
    label: "proof accepted"
)
.Route(
    on: song.Proofreader.Success,
    when: state => !state.ProofreaderAccepted,
    to: song.Songwriter,
    label: "changes requested"
)
.Route(on: song.Proofreader.Failed, to: song.Failed, label: "agent failed")
```

`song.Failed` is created with `PipelineNodes.Failed<SongwriterState>(...)`. It
preserves the failure evidence and terminates with Tandem's failed disposition.
Both completion and failure furniture expose `IPipelineNode<TState>`; ordinary
samples and composition records do not use `IRawPipelineNode`.

## Three Inferred Step Forms

The source generator infers a step's authoring mode from its `ExecuteAsync`
signature. There is no mode setting and no universal result-union requirement.

```csharp
ValueTask ExecuteAsync(TState state, CancellationToken cancellationToken)
```

Pass-through: preserves state, produces standard success, and exposes no
outcome selectors.

```csharp
ValueTask<TState> ExecuteAsync(TState state, CancellationToken cancellationToken)
```

State-updating: uses the returned state, produces standard success, and exposes
no outcome selectors.

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

A standard `Failed` result is recoverable pipeline data only when an unconditional
route handles it or at least one conditional route matches its failed state. An
unhandled `Failed` ends the run as failed. Exceptions are undeclared faults, and
cancellation remains cancellation; neither follows an ordinary output route.

## State-First Agents And Policies

Agent definitions are immutable application configuration registered once in DI.
They use state-first callbacks throughout and capture no pipeline build or run:

```csharp
agents
    .Create<SongwriterState>(id, id, instructions, client)
    .WithMessage(state =>
        $"Brief: {state.Brief}\nLyrics: {state.Lyrics}\n"
        + $"Lint: {state.LintFeedback}\nProofreader: {state.ProofreaderFeedback}"
    )
    .WithOutput(new SongDecisionValidator(), SongwriterPolicies.ApplySong)
    .WithSessionPolicy(SongwriterPolicies.StartFresh)
    .Build();
```

`WithMessage` receives `TState`; validated output application returns `TState`.
Successful validated application produces canonical `Success`. Tandem transports
run identity, sessions, usage, invocation counts, outcomes, and replay metadata
internally. Ordinary user code cannot unwrap an agent operation or copy an
execution envelope.

## Explicit Routing

Use an unconditional output route for serial flow when every produced result is
allowed to continue:

```csharp
.Route(on: support.LoadAccount, to: support.Resolve, label: "account loaded")
```

Use canonical outcome selectors and state predicates when execution outcome or a
domain fact controls the successor:

```csharp
.Route(on: song.Lint, when: state => state.LintFeedback is null, to: song.Proofreader)
.Route(on: song.Lint, when: state => state.LintFeedback is not null, to: song.Songwriter)
.Route(on: song.Songwriter.Success, to: song.Lint, label: "song written")
```

Normal predicates receive state:

```csharp
.Route(
    when: state => state.FinalDisposition == "closed",
    from: support.CustomerReply,
    to: support.Close,
    label: "customer confirmed"
)
```

Do not mix unconditional and outcome-specific routes from one source: both would
match the same output. Tandem rejects that accidental fan-out. Use
`RouteWithContext` only when an advanced route genuinely requires the complete
execution message.

Request-port expansion and raw MAF nodes are internal. Generated steps and SDK
furniture expose only typed `IPipelineNode<TState>` boundaries.

## Progressive Samples

The public SDK is one progressive journey rather than separate basic and advanced
programming models:

- **Songwriter** proves state-updating steps, state-owned semantic branches,
  unconditional serial routes, and agent execution without workspace or runtime
  plumbing.
- **Support** adds consumer-owned account lookup, typed state transitions, and a
  durable typed customer request/response handoff.
- **Debate** adds revision loops, explicit retained/reset sessions, a lifecycle
  action with a receipt-backed state transition, and teardown based on block
  evidence.
- **Delivery** adds custom blocks, workspaces and mutation policy, checkpoints,
  tools, verification commands, observations, and durable human handoff.

### Durable Support Handoff

Support declares one semantic interaction with state-first transformations:

```csharp
var customerReply = PipelineNodes.WaitFor<SupportState, CustomerQuestion, CustomerReply>(
    SupportIds.CustomerReply,
    SupportPolicies.BuildCustomerQuestion,
    SupportPolicies.ApplyCustomerReply
);
```

Composition routes to and from that interaction. Tandem privately expands it to
MAF's request, port, and resume executors, saving and restoring the complete
execution envelope around suspension.

### Debate Sessions And Teardown

Debate keeps ordinary session policy state-first:

```csharp
public static AgentSessionDecision RetainRevisionContext(DebateState _) =>
    new(AgentSessionAction.Continue, "Retain critic context across revision rounds.");
```

Its judge conversation policy intentionally uses advanced block evidence:

```csharp
public static AgentConversationDecision DiscardJudgeAfterVerdict(
    PipelineMessage<DebateState> _,
    BlockOutcome __
) => new(AgentConversationRetention.Discard, "The verdict closes the judge conversation.");
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

Import `Tandem.Advanced` to opt into envelope-aware agent policies and operations.
Delivery uses `PipelineOperation.RunStateAsync` and `RunOutcomeAsync` to preserve
runtime updates without manually copying envelope fields. The public descriptor
types hidden from IntelliSense are an opaque cross-assembly ABI used by generated
code, not a node-authoring hierarchy.

## Inspect And Run

`Inspect()` describes the exact built workflow, including start and output nodes,
routes, conditions, request ports, and Mermaid/DOT diagrams:

```csharp
var inspection = composition.Build().Inspect();
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
