# Pipeline Authoring

Tandem exposes one progressive authoring model over Microsoft Agent Framework.
Pipeline packages own typed durable state, steps, prompts, policies, capabilities,
semantic results, and explicit routes. Tandem generates adapters and owns the
execution envelope, durability, sessions, agent loops, tool dispatch, suspension,
and replay without exposing MAF to consumers.

Use the compiled examples in this order:

1. [`Tandem.Sample.Songwriter`](../samples/Tandem.Sample.Songwriter) for the
   minimal step, agent, branch, loop, and composition model.
2. [`Tandem.Sample.Support`](../samples/Tandem.Sample.Support) for deterministic
   ports and durable typed request/response handoff.
3. [`Tandem.Sample.Debate`](../samples/Tandem.Sample.Debate) for sessions,
   lifecycle actions, receipts, and teardown.
4. [`Tandem.Delivery`](../src/Tandem.Delivery) for custom blocks, workspace,
   checkpoints, tools, observations, verification, and human handoff.

## Author Journey

1. Reference `Tandem` and add `Tandem.Generators` as an analyzer. Reference
   `Dunet` only when a step needs custom branch results.
2. Define one immutable, serializable `<Name>State` containing durable facts,
   never services, framework contexts, or a mutable state bag.
3. Implement each generated step as a partial class marked with
   `[PipelineStage("stable-id")]`.
4. Give `ExecuteAsync` a `TState` and `CancellationToken`; select one of the four
   inferred return forms below.
5. Configure agents per pipeline build with state-first prompts, parsers, and
   ordinary policies.
6. Put executable instances in a DI-constructed `<Name>Steps` record or factory.
7. Declare every successor in `<Name>Composition` with `Route`; fluent order does
   not imply edges.
8. Test inspection, serialization, in-process execution, and durable execution
   for the capabilities the pipeline uses.

## Inferred ExecuteAsync Forms

The generator recognizes exactly these ordinary signatures.

### Pass-Through

```csharp
ValueTask ExecuteAsync(TState state, CancellationToken cancellationToken)
```

Completion preserves state and produces standard success. The generated step has
no `.Result` selectors. Songwriter's compiled terminal is:

```csharp
[PipelineStage(CompleteSongStage.StepId)]
public sealed partial class CompleteSongStage
{
    public const string StepId = "complete";

    public ValueTask ExecuteAsync(SongwriterState _, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
```

### State-Updating

```csharp
ValueTask<TState> ExecuteAsync(TState state, CancellationToken cancellationToken)
```

Completion replaces only durable state with the returned value and produces
standard success. The generated step has no `.Result` selectors. Support's
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

### Custom Result

```csharp
ValueTask<TCustomResult> ExecuteAsync(
    TState state,
    CancellationToken cancellationToken
)
```

`TCustomResult` is an authored nested Dunet union. It replaces the standard
authored result and exposes only its declared cases. Use custom Dunet only where
the graph branches on meaningful domain vocabulary. Songwriter's compiled lint
step is:

```csharp
[PipelineStage(LintStage.StepId)]
public sealed partial class LintStage
{
    public const string StepId = "lint";

    [Union(EnableImplicitConversions = false)]
    public partial record LintResult
    {
        public partial record Passed(SongwriterState State);
        public partial record Failed(SongwriterState State);
    }

    public ValueTask<LintResult> ExecuteAsync(
        SongwriterState state,
        CancellationToken _
    )
    {
        state = SongwriterPolicies.Lint(state);
        return ValueTask.FromResult<LintResult>(
            state.LintFeedback is null ? new LintResult.Passed(state) : new LintResult.Failed(state)
        );
    }
}
```

`LintResult.Failed` is custom branch vocabulary, distinct from Tandem's standard
`Outcome<TState>.Failed` and its unhandled-failure semantics.

## Agent Operations

`[PipelineStage]` makes a class executable and routable; it does not imply model
execution. Build an `AgentOperation<TState>` explicitly from the DI-owned
`AgentRuntime`. Support's compiled classifier configuration is:

```csharp
var classifier = agentRuntime
    .Create<SupportState>(
        ClassifyTicketAgent.StepId,
        "support-classifier",
        SupportPrompts.Classifier,
        options.ClassifierClient
    )
    .WithMessage(SupportPrompts.ClassificationMessage)
    .WithStructuredOutput(
        SupportPolicies.ParseClassification,
        chat => chat.ResponseFormat = ChatResponseFormat.ForJsonSchema<ClassificationDecision>()
    )
    .WithSessionPolicy(SupportPolicies.StartClassificationFresh)
    .Build(context);
```

The callbacks above are state-first:

- `WithMessage` accepts `Func<TState, string>`.
- `StructuredOutputParser<TState>` accepts assistant text and `TState`.
- `AgentSessionPolicy<TState>` accepts `TState`.
- `WithWorkspace` accepts state-first path and mutation predicates.
- ordinary `Route` predicates accept `Func<TState, bool>`.
- durable request creation and response application are state-first.

Agent construction is per pipeline build because observers and update callbacks
are build-specific. DI owns stable clients and dependencies, not operations that
capture an earlier `PipelineBuildContext`.

An agent maps operation evidence into custom results only when branching needs
it. Songwriter's compiled proofreader uses:

```csharp
return await operation.RunAsync<ProofreaderResult>(
    state,
    result =>
        result.Outcome.Kind == SongwriterPolicies.ProofAcceptedOutcome
            ? new ProofreaderResult.Accepted(result.State)
            : new ProofreaderResult.ChangesRequested(result.State),
    failure => new ProofreaderResult.Failed(state, failure),
    cancellationToken
);
```

The mapper receives `OperationResult<TState>`, whose public shape is:

```csharp
public sealed record OperationResult<TState>(TState State, OperationOutcome Outcome);

public sealed record OperationOutcome(
    string Kind,
    string Summary,
    JsonElement Payload
);
```

It can interpret legitimate block evidence without receiving sessions, usage,
profiles, invocation counts, or the complete execution envelope.
The failure mapper is mandatory for custom agent results: infrastructure failure
must remain distinct from a semantic branch such as `ChangesRequested`.

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
.Route(on: song.Lint.Result.Passed, to: song.Proofreader, label: "lint passed")
.Route(on: song.Lint.Result.Failed, to: song.Songwriter, label: "lint failed")
```

A result-specific condition remains state-first:

```csharp
.Route(
    on: delivery.CaptureCandidate.Result.Captured,
    when: HasVerificationCommands,
    to: delivery.Verification,
    label: "verification configured"
)
```

Do not mix unconditional and result-specific route modes for one source; both
would match the same output and create accidental fan-out. Tandem rejects the
mix. Multiple result cases and deliberately exclusive conditions for one case
remain valid.

Request and custom `IPipelineNode` edges use `from` because those nodes are not
generated steps:

```csharp
.Route(
    from: support.CustomerReply.Request,
    to: support.CustomerReply.Port,
    label: "wait for customer"
)
```

`RouteWithContext` is the explicit advanced variant for predicates that genuinely
need `PipelineMessage<TState>`. Do not use it merely to read state.

## Durable Request Handoffs

Support creates a typed request handoff with the compiled API:

```csharp
var customerReply = PipelineNodes.Request<SupportState, CustomerQuestion, CustomerReply>(
    SupportIds.AskCustomer,
    SupportIds.CustomerReply,
    SupportIds.ApplyReply,
    SupportPolicies.BuildCustomerQuestion,
    SupportPolicies.ApplyCustomerReply
);
```

The two authored transforms have these contracts:

```csharp
Func<TState, TRequest> createRequest
Func<TState, TResponse, TState> applyResponse
```

The returned handoff exposes `Request`, `Port`, and `Resume`. Tandem persists and
restores the complete execution envelope, applies the response state transition,
and records resume evidence. Ordinary userland does not serialize or reconstruct
`PipelineMessage<TState>`.

Support routes after resume with state-first predicates:

```csharp
.Route(
    when: state => state.FinalDisposition == "closed",
    from: support.CustomerReply.Resume,
    to: support.Close,
    label: "customer confirmed"
)
```

## Advanced Block Authoring

Ordinary steps, prompts, parsers, state transitions, and predicates use `TState`.
Advanced block implementations and runtime policies may intentionally use
`PipelineMessage<TState>` and `BlockOutcome` when execution evidence is their
purpose. The current compiled core shapes are:

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

Debate demonstrates a narrow advanced policy while keeping ordinary policies
state-first:

```csharp
public static AgentSessionDecision RetainRevisionContext(DebateState _) =>
    new(AgentSessionAction.Continue, "Retain critic context across revision rounds.");

public static AgentTeardownDecision ReleaseJudgeAfterVerdict(
    PipelineMessage<DebateState> _,
    BlockOutcome __
) => new(true, true, "Release judge bookkeeping after an accepted verdict.");
```

Delivery is the acceptance consumer for the advanced layer. Its custom workspace,
candidate-capture, verification, terminal, and human-input blocks operate on the
envelope because preserving or observing execution evidence is part of those
blocks. Generated ordinary-step adapters still own envelope transport.

`PipelineOperation.RunAsync` is available when an authored generated step adapts
an advanced block into semantic routing. Delivery's workspace stage uses:

```csharp
return await PipelineOperation.RunAsync<DeliveryState, PrepareWorkspaceResult>(
    () => operation.ExecuteAsync(pipeline, cancellationToken),
    result =>
        result.Outcome.Kind == OutcomeKinds.WorkspacePrepared
            ? new PrepareWorkspaceResult.Prepared(result.State)
            : new PrepareWorkspaceResult.Unexpected(result.State)
);
```

This is advanced block integration, not the default shape for ordinary stages.

## Progressive Capability Journey

- Songwriter: simple state, pass-through/state-updating steps, agents, custom
  branch results, unconditional routes, and a review loop.
- Support: consumer-owned deterministic I/O and durable typed suspension/resume.
- Debate: revision sessions, lifecycle actions and receipts, and evidence-aware
  teardown.
- Delivery: custom blocks, workspace mutation policy, checkpoints, tools,
  observations, verification, planner/reviewer lifecycle outcomes, and human
  handoff.

All four use the same generated steps, agent builder, typed state, and fluent
composition model. Later examples add capabilities; they do not replace the API.

## Inspection

Inspect the exact executable graph after building it:

```csharp
var inspection = composition.Build(new PipelineBuildContext()).Inspect();
Console.WriteLine(inspection.Mermaid);
Console.WriteLine(inspection.Dot);
```

Inspection includes composition metadata, start and output steps, request-port
identities and types, routes and condition presence, Mermaid, and Graphviz DOT.
Tandem does not maintain a second graph AST.
