# Pipeline Authoring

Tandem exposes one progressive authoring model over Microsoft Agent Framework.
Pipeline packages own typed state, steps, prompts, policies, capabilities,
semantic results, and explicit routes. Tandem generates adapters and owns its
execution envelope and live suspension without exposing MAF to consumers. MAF
owns orchestration, agent loops, sessions, and tool dispatch.

Use the compiled examples in this order:

1. [`Tandem.Sample.Songwriter`](../samples/Tandem.Sample.Songwriter) for the
   minimal step, agent, branch, loop, and composition model.
2. [`Tandem.Sample.Support`](../samples/Tandem.Sample.Support) for deterministic
   ports and typed request/response handoff.
3. [`Tandem.Sample.Debate`](../samples/Tandem.Sample.Debate) for sessions,
   local capabilities and teardown.
4. [`Tandem.Delivery`](../src/Tandem.Delivery) for custom blocks, workspace,
   checkpoints, tools, observations, verification, and human handoff.

## Author Journey

1. Reference `Tandem` and add `Tandem.Generators` as an analyzer.
2. Define one immutable `<Name>State` containing lifecycle facts,
   never services, framework contexts, or a mutable state bag.
3. Implement each generated step as a partial class marked with
   `[PipelineStage("stable-id")]`.
4. Give `ExecuteAsync` a `TState` and `CancellationToken`; select one of the three
   inferred return forms below.
5. Configure agents per pipeline build with state-first prompts, parsers, and
   ordinary policies.
6. Put executable instances in a typed `<Name>Participants` record. Add a
   `<Name>ParticipantsFactory` when assembly needs injected clients or infrastructure.
7. Declare every successor in `<Name>Composition` with `Route`; fluent order does
   not imply edges.
8. Test inspection, typed state behavior, and real in-process execution for the
   capabilities the pipeline uses.

## Canonical Pipeline Shape

A pipeline package should be copyable as an authored feature, not assembled as a
flat collection of framework files. Start with this spine:

```text
MyPipeline/
├── MyPipelineState.cs
├── MyPipelineParticipants.cs
├── MyPipelineParticipantsFactory.cs  # when assembly is non-trivial
├── MyPipelineComposition.cs
├── MyPipelineRegistration.cs
├── Agents/
├── Capabilities/
├── Stages/
├── Interactions/
├── Observation/
└── Infrastructure/
```

The root files explain the complete pipeline:

- `State` contains immutable lifecycle facts and semantic routing decisions.
- `Participants` is the typed inventory of agents, deterministic stages,
  interactions, and terminal nodes available to composition.
- `ParticipantsFactory`, when needed, assembles those immutable definitions from
  DI-provided clients and infrastructure. Small samples may construct participants
  directly in `<Name>Definitions`.
- `Composition` declares the complete route map.
- `Registration` wires clients, infrastructure, definitions, and hosting entry
  points.

Optional folders are organized by authored meaning:

- `Agents/<Role>` owns that agent's definition, instructions, state-derived
  message, typed output model, validator, and policies.
- `Capabilities` owns typed capability requests, validators, summaries, and
  state transitions.
- `Stages/<Concern>` owns deterministic application operations and their narrow
  infrastructure dependencies.
- `Interactions/<Name>` owns a semantic request/response contract and the state
  transitions around waiting and resumption.
- `Observation` owns pipeline-specific projections for hosts and operators.
- `Infrastructure` owns repository, process, storage, and external-system
  mechanics used by authored stages or policies.

Do not add empty folders merely to satisfy the template. A minimal pipeline may
need only the spine and `Agents`; a richer pipeline grows the optional areas when
their concepts are real.

Use [`Tandem.Delivery`](../src/Tandem.Delivery) as the complete advanced example.
Its repository Harness contract is Delivery-owned application configuration, not
part of the generic template.

## Inferred ExecuteAsync Forms

The generator recognizes exactly these ordinary signatures.

### Pass-Through

```csharp
ValueTask ExecuteAsync(TState state, CancellationToken cancellationToken)
```

Completion preserves state and produces standard success. Empty terminal classes
are replaced by an SDK node:

```csharp
var complete = PipelineNodes.Complete<SongwriterState>("complete");
```

### State-Updating

```csharp
ValueTask<TState> ExecuteAsync(TState state, CancellationToken cancellationToken)
```

Completion replaces only pipeline state with the returned value and produces
standard success. The generated step has no outcome selectors. Support's
compiled deterministic stage is:

```csharp
[PipelineStage(LoadAccountStage.StepId)]
public sealed partial class LoadAccountStage(IAccountLookup accountLookup)
{
    public const string StepId = "support-load-account";

    public async ValueTask<SupportState> ExecuteAsync(
        SupportState state,
        CancellationToken cancellationToken
    )
    {
        var context = await accountLookup.LoadAsync(state, cancellationToken);
        return state with { AccountContext = context };
    }
}
```

### Standard Outcome

```csharp
ValueTask<Outcome<TState>> ExecuteAsync(
    TState state,
    CancellationToken cancellationToken
)
```

The step returns Tandem's standard `Success` or `Failed` and exposes exactly
those typed selectors. The compiled core API is:

```csharp
public static class StandardOutcomeKinds
{
    public const string Success = "tandem.success";
    public const string Failed = "tandem.failed";
}

public abstract record Outcome<TState>
{
    private Outcome() { }

    public sealed record Success(TState State) : Outcome<TState>;
    public sealed record Failed(TState State, FailureEvidence Failure) : Outcome<TState>;
}

public sealed record FailureEvidence(string Code, string Summary, string? Detail = null);
```

Agents can use this form directly:

```csharp
return await operation.RunAsync(state, cancellationToken);
```

A `Failed` result is recoverable data when an unconditional route handles it or at
least one conditional route matches its failed state. Otherwise, the run ends
with failed disposition and retains the failure evidence. Exceptions are
undeclared execution faults. Cancellation is cancellation. Neither is a declared
`Failed` result.

### Domain Branches

Domain decisions are pipeline state facts, not additional step outcome types.
Songwriter's lint step records its decision in state:

```csharp
[PipelineStage(LintStage.StepId)]
public sealed partial class LintStage
{
    public const string StepId = "lint";

    public ValueTask<SongwriterState> ExecuteAsync(
        SongwriterState state,
        CancellationToken _
    ) => ValueTask.FromResult(SongwriterPolicies.Lint(state));
}
```

Composition branches on `LintFeedback`; standard `Failed` remains reserved for
declared execution failure and its unhandled-failure semantics.

## Agent Definitions

`[PipelineStage]` makes a class executable and routable; it does not imply model
execution. Build an immutable agent definition directly with `Agent.Create`. Support's
compiled classifier configuration is:

```csharp
var classifier = agentRuntime
    .Create<SupportState>(
        "support-classify",
        SupportPrompts.Classifier,
        options.ClassifierClient
    )
    .WithMessage(SupportPrompts.ClassificationMessage)
    .WithOutput(new ClassificationDecisionValidator(), SupportPolicies.ApplyClassification)
    .Build();
```

The returned `AgentDefinition<SupportState>` is directly composable and exposes
`classifier.Success` and `classifier.Failed`. Do not add an authored
`ExecuteAsync` class that only forwards model execution.

The callbacks above are state-first:

- `WithMessage` accepts `Func<TState, string>`.
- `WithOutput<T>` owns schema, deserialization, correction, and raw failure evidence.
- agents start fresh by default; `.ContinueSession()` explicitly retains conversation.
- `WithWorkspace` accepts state-first path and mutation predicates.
- ordinary `Route` predicates accept `Func<TState, bool>`.
- request creation and response application are state-first.

Definitions are immutable DI-owned configuration. Tandem binds live updates by
run ID during execution; definitions capture no pipeline build or run.

Agent output transitions put semantic decisions in state. Infrastructure failure
returns canonical `Failed`; composition does not reinterpret it as a domain branch
such as `ChangesRequested`.

## Composition And Routing

Every successor is explicit. An unconditional output route starts with the source
step itself:

```csharp
.Route(on: song.Songwriter, to: song.Lint, label: "song written")
```

It matches any produced output from that step, including a routed standard
`Failed`; it does not run after an exception or cancellation.

A result-specific route starts with a generated selector:

```csharp
.Route(on: song.Lint, when: state => state.LintFeedback is null, to: song.Proofreader)
.Route(on: song.Lint, when: state => state.LintFeedback is not null, to: song.Songwriter)
```

A result-specific condition remains state-first:

```csharp
.Route(
    on: delivery.CaptureCandidate.Success,
    when: HasVerificationCommands,
    to: delivery.Verification,
    label: "verification configured"
)
```

Do not mix unconditional and result-specific route modes for one source; both
would match the same output and create accidental fan-out. Tandem rejects the
mix. Multiple result cases and deliberately exclusive conditions for one case
remain valid.

Semantic interactions and custom `IPipelineNode` edges use `from` because those
nodes are not generated steps:

```csharp
.Route(
    from: support.CustomerReply,
    to: support.Close,
    label: "customer confirmed"
)
```

## Live Request Handoffs

Support creates a typed request handoff with the compiled API:

```csharp
var customerReply = PipelineNodes.WaitFor<SupportState, CustomerQuestion, CustomerReply>(
    SupportIds.CustomerReply,
    SupportPolicies.BuildCustomerQuestion,
    SupportPolicies.ApplyCustomerReply
);
```

The two authored transforms have these contracts:

```csharp
Func<TState, TRequest> createRequest
Func<TState, TResponse, TState> applyResponse
```

Composition sees one semantic handoff. Tandem privately expands it into MAF's
request, port, and resume executors, preserves the execution envelope, applies the
response transition, and records continuation evidence. The wait is asynchronous
and lives only as long as the initiating process.

Support routes after resume with state-first predicates:

```csharp
.Route(
    when: state => state.FinalDisposition == "closed",
    from: support.CustomerReply,
    to: support.Close,
    label: "customer confirmed"
)
```

## Advanced Block Authoring

Ordinary steps, prompts, parsers, state transitions, and predicates use `TState`.
Advanced block implementations and runtime policies use narrow Advanced-owned
contexts. The complete execution envelope is internal. Custom operations use:

```csharp
public sealed record PipelineOperationContext<TState>(/* run ID, state, latest outcome */);

public sealed record OperationOutcome(
    string Kind,
    string BlockId,
    string Summary,
    JsonElement Payload = default,
    TimeSpan Duration = default
);

public sealed record OperationResult<TState>(TState State, OperationOutcome Outcome);
```

Debate demonstrates a narrow advanced policy while keeping ordinary policies
state-first:

```csharp
builder.ContinueSession();

public static AgentConversationDecision DiscardJudgeAfterVerdict(
    AgentMessageContext<DebateState> _,
    AgentMessageOutcome __
) => new(AgentConversationRetention.Discard);
```

Delivery is the acceptance consumer for the advanced layer. Workspace preparation
and candidate capture are ordinary generated stages over application services.
Verification alone uses `PipelineOperationContext<TState>` because it emits
run-owned command observations while executing.

`PipelineOperation.RunOutcomeAsync` is available after importing
`Tandem.Advanced` when a generated step adapts an advanced operation. Delivery's
verification stage uses this bridge; ordinary stages should not request runtime
context merely to access their typed state.

```csharp
return await PipelineOperation.RunOutcomeAsync(...);
```

This is advanced block integration, not the default shape for ordinary stages.

## Progressive Capability Journey

- Songwriter: simple state, state-updating steps, agents, state-owned branches,
  unconditional routes, and a review loop.
- Support: consumer-owned deterministic I/O and typed live suspension/continuation.
- Debate: revision sessions, local typed capabilities, and evidence-aware teardown.
- Delivery: typed accepted facts, workspace mutation policy, checkpoints, tools,
  observations, verification, planner/reviewer lifecycle outcomes, and human
  handoff.

All four use the same generated steps, agent builder, typed state, and fluent
composition model. Later examples add capabilities; they do not replace the API.

A capability is declared and registered once. Authors provide its semantic tool
name, typed request validation, summary, and typed state transition:

```csharp
var verdict = AgentCapabilities.Create<DebateState, SubmitVerdict>(
    "submit_verdict",
    "Submit the final verdict and end the judge turn.",
    new SubmitVerdictValidator(),
    request => $"Verdict submitted: {request.Verdict}",
    DebatePolicies.ApplyVerdict
);
```

`.WithCapability(verdict)` is ordinary Core authoring. Tandem binds
the attached descriptor as a local MAF `AIFunction` for each invocation and owns
run, block, invocation, and capability identity plus atomic accepted-call
ownership. Invalid calls do not transition state or terminate the turn.

Feature registration may store the immutable capability as application
configuration (`services.AddSingleton(verdict)`), but execution follows direct
attachment to the agent definition rather than DI discovery or transport
registration. Advanced `.WithAcceptance(...)` decorates the same capability with
a runtime-aware callback for facts that must commit before Core applies the state
transition and routing continues.

A capability is a terminal semantic transition, not a repeatable general-purpose
tool. An agent may expose several capabilities, but at most one is accepted per
pipeline visit. If the agent also has typed output, Tandem validates and applies
that output only when no capability was accepted.

`PipelineNodeDescriptor` remains public only because generated partial classes
compile in consumer assemblies. It is hidden from IntelliSense and is an opaque
generated-code ABI; `IRawPipelineNode` and raw node factories are internal.

## Inspection

Inspect Tandem's semantic projection of the executable graph after building it:

```csharp
var inspection = composition.Build().Inspect();
Console.WriteLine(inspection.Mermaid);
Console.WriteLine(inspection.Dot);
```

Inspection includes composition metadata, start and output steps, typed semantic
interactions, routes and condition presence, Mermaid, and Graphviz DOT.
Tandem does not maintain a second graph AST.
