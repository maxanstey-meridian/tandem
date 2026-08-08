# Cohesive Authoring Units Plan

Status: agreed direction; not yet implemented.

This is the single active plan for the next authoring-language campaign. Durable
ledger and gate work is being implemented separately through
[`DURABLE_LEDGER_AND_GATES_PLAN.md`](DURABLE_LEDGER_AND_GATES_PLAN.md) and must land
before this campaign edits overlapping runtime files.

## Objective

Hide complicated SDK mechanics behind small authored definitions while preserving
honest ownership:

```text
one application concept is physically cohesive
separate CLR types keep separate responsibilities
semantic values remain small records
state owns pure state transitions
the public call site reads like the application
runtime mechanics remain below the seam
```

Apply this pattern only to concepts currently assembled through arbitrary companion
values or positional arguments:

1. typed agent outputs;
2. semantic agent capabilities;
3. completion and failure terminals.

Do not turn every public API into a definition hierarchy.

## Shared Invariants

1. Definition objects collect authored instructions, validation, examples, and
   summaries that otherwise depend on naming convention or argument position.
2. Semantic request and response values remain small records.
3. A concept may be one file or feature unit without becoming one fat CLR type.
4. Pure state transformations are methods on the state that owns the invariants.
5. Binding remains visibly functional:

   ```csharp
   (state, value) => state.RecordSomething(value)
   ```

6. Core definitions contain application meaning, not run IDs, invocation IDs,
   sessions, provider types, serialized payloads, tool observations, or runtime
   outcomes.
7. Advanced may decorate a completed Core definition with execution-aware
   acceptance or observation.
8. Invalid or rejected input never applies a state transition.
9. Accepted input applies its state transition exactly once.
10. No compatibility shim is added before v1 unless a concrete shipped consumer
    requires one.

## 1. Typed Agent Outputs

### Authored Unit

```text
CodingDecisionOutput.cs
├── CodingDecision                 semantic value returned by the model
├── CodingDecisionOutput           Tandem output definition
├── CodingDecisionValidator        intrinsic validation
└── typed examples                 representative responses
```

`CodingDecision` is plain application data. `CodingDecisionOutput` is the complete
definition Tandem consumes. They are colocated but remain separate types.

Do not nest Tandem metadata inside the semantic record. Do not attach an arbitrary
static `Output`, `Definition`, or `Contract` property to it.

### Proposed API

```csharp
public interface IAgentOutputDefinition<TState, TOutput>
{
    string Instructions { get; }

    IValidator<TOutput> Validator { get; }

    IValidator<TOutput>? ValidatorFor(TState state) => null;

    IReadOnlyList<AgentOutputExample<TOutput>> Examples(TState state) => [];
}

public sealed record AgentOutputExample<TOutput>(
    string Input,
    TOutput Output);
```

```csharp
public AgentBuilder<TState> WithOutput<TOutput>(
    IAgentOutputDefinition<TState, TOutput> output,
    Func<TState, TOutput, TState> apply);
```

Keep the name `WithOutput`. It names the authored concept rather than enumerating
parsing, schema, validation, correction, acceptance, and mapping mechanics.

### Example

```csharp
// CodingDecisionOutput.cs

public sealed record CodingDecision(
    [property: Description(
        "A useful explanation of the proposed change, including important files and behavior.")]
    string ProposedChange);

public sealed class CodingDecisionOutput
    : IAgentOutputDefinition<CodingState, CodingDecision>
{
    public string Instructions =>
        "Return a concrete proposed implementation of the requested coding task.";

    public IValidator<CodingDecision> Validator { get; } =
        new CodingDecisionValidator();

    public IReadOnlyList<AgentOutputExample<CodingDecision>> Examples(
        CodingState state
    ) =>
    [
        new(
            Input: state.Instructions,
            Output: new CodingDecision(
                "Add a Greeting component above the existing call to action. "
                    + "Preserve the current typography and cover the new rendering "
                    + "branch with a component test.")),
    ];
}

public sealed class CodingDecisionValidator : AbstractValidator<CodingDecision>
{
    public CodingDecisionValidator()
    {
        RuleFor(decision => decision.ProposedChange)
            .NotEmpty()
            .MinimumLength(20);
    }
}
```

State owns the transition:

```csharp
public CodingState RecordProposedChange(CodingDecision decision) =>
    this with
    {
        ProposedChange = decision.ProposedChange,
        Approved = false,
        ReviewNotes = null,
    };
```

Call site:

```csharp
.WithOutput(
    new CodingDecisionOutput(),
    (state, decision) => state.RecordProposedChange(decision))
```

### Contextual Validation

Intrinsic validation belongs to the output concept and is reusable independently.
State-dependent validation also belongs to the definition, not every consuming
agent call site:

```csharp
public sealed class ReviewDecisionOutput
    : IAgentOutputDefinition<DeliveryState, ReviewDecision>
{
    public string Instructions =>
        "Return an evidence-grounded decision covering every packet outcome.";

    public IValidator<ReviewDecision> Validator { get; } =
        new ReviewDecisionValidator();

    public IValidator<ReviewDecision> ValidatorFor(DeliveryState state) =>
        new ReviewOutcomeCoverageValidator(
            state.Packet.Outcomes.Select(outcome => outcome.Id));

    public IReadOnlyList<AgentOutputExample<ReviewDecision>> Examples(
        DeliveryState state
    ) =>
    [
        BuildAcceptedExample(state.Packet.Outcomes),
    ];
}
```

The call remains:

```csharp
.WithOutput(
    new ReviewDecisionOutput(),
    (state, decision) => state.RecordReview(decision))
```

### Description And Example Ownership

- Definition `Instructions` tells the agent what complete response to return.
- BCL `[Description]` attributes explain individual semantic-value fields.
- Agent instructions explain the agent's role and when to produce the output.
- Typed examples demonstrate useful responses without duplicating JSON in prompts.

Microsoft.Extensions.AI already maps `DescriptionAttribute` into JSON Schema. Do
not create Tandem-specific field-description attributes.

Do not put examples into JSON Schema. Provider support is inconsistent and schema
examples are annotations rather than portable few-shot messages.

Below the Core seam, serialize each typed example with the real output serializer
and provide maintained chat turns:

```text
user:      example.Input
assistant: serialized example.Output
```

Example/session rules:

- validate examples intrinsically and contextually before model invocation;
- add examples only to a fresh session;
- do not duplicate them on retained-session visits;
- re-add them after intentional reset or model-profile change;
- do not resend them during correction;
- do not add ordinary output examples in checkpoint-only mode.

### Output Runtime Ordering

1. Build the authored user message from state.
2. Obtain and validate state-derived examples.
3. Add examples when the session is fresh.
4. Send one output schema, instructions, and serializer contract.
5. Deserialize the model response into `TOutput`.
6. Run intrinsic validation.
7. Run contextual validation when present.
8. Run Advanced synchronous acceptance.
9. Run Advanced asynchronous acceptance.
10. Apply the state transition exactly once.
11. Emit canonical `Success` and route from updated typed state.

Preserve the existing one-correction, same-session behavior. Correction observes
the same pre-transition state and never duplicates examples.

Use one immutable serializer configuration for schema generation, examples,
response deserialization, and accepted-output payload serialization.

## 2. Agent Capabilities

### Authored Unit

```text
AskPlannerCapability.cs
├── AskPlannerRequest
├── AskPlannerCapability
└── AskPlannerRequestValidator
```

One application permission is currently split across tool name, instructions,
validator, summary, and state-transition arguments. The definition should make
those companions mechanically cohesive.

### Proposed API

```csharp
public interface IAgentCapabilityDefinition<TState, TRequest>
    where TRequest : class
{
    string ToolName { get; }

    string Instructions { get; }

    IValidator<TRequest> Validator { get; }

    IValidator<TRequest>? ValidatorFor(TState state) => null;

    string Summarize(TRequest request);
}
```

```csharp
public static AgentCapability<TState, TRequest> Create<TState, TRequest>(
    IAgentCapabilityDefinition<TState, TRequest> capability,
    Func<TState, TRequest, TState> apply)
    where TRequest : class;
```

Example:

```csharp
public sealed class AskPlannerCapability
    : IAgentCapabilityDefinition<DeliveryState, AskPlannerRequest>
{
    public string ToolName => "ask_planner";

    public string Instructions =>
        "Ask the planner agent for guidance and end the current turn.";

    public IValidator<AskPlannerRequest> Validator { get; } =
        new AskPlannerRequestValidator();

    public string Summarize(AskPlannerRequest request) =>
        $"Planner asked: {request.Question}";
}
```

```csharp
var askPlanner = AgentCapabilities
    .Create(
        new AskPlannerCapability(),
        (state, request) => state.RecordPlannerRequest(request))
    .WithAcceptance(records.AcceptPlannerRequestAsync);
```

Core owns semantic permission and transition. Advanced acceptance continues to
own durable recording or other I/O before state application.

Capability ordering remains:

1. deserialize request;
2. intrinsic validation;
3. contextual validation;
4. authored summary;
5. reserve the single lifecycle transition;
6. Advanced asynchronous acceptance;
7. apply state exactly once;
8. commit accepted capability and end the turn.

## 3. Terminal Definitions

### Desired Authoring

```csharp
PipelineNodes.Complete(new RunReady())
PipelineNodes.Failed(new RunFailed())
```

This hides generic state typing and terminal mechanics without hiding topology.

### Proposed API

```csharp
public interface IPipelineCompletion<TState>
{
    string Id { get; }

    string Summarize(TState state);

    TState Complete(TState state) => state;
}

public interface IPipelineFailure<TState>
{
    string Id { get; }

    string Summarize(TState state);

    TState Fail(TState state) => state;
}
```

```csharp
public static IPipelineNode<TState> Complete<TState>(
    IPipelineCompletion<TState> completion);

public static IPipelineNode<TState> Failed<TState>(
    IPipelineFailure<TState> failure);
```

Keep string-only convenience overloads for anonymous terminals. Remove positional
transition/outcome-kind/summary overloads.

Definitions own semantic ID, state-derived summary, and an optional pure state
transition. Tandem owns terminal classification:

```text
completion -> PipelineRunStatus.Succeeded + StandardOutcomeKinds.Success
failure    -> PipelineRunStatus.Failed    + StandardOutcomeKinds.Failed
```

Definitions do not receive source step IDs, previous outcome kinds, payloads, or
runtime envelopes. Failure summaries come from typed state with a stable fallback.
Infrastructure faults remain exceptions.

Delivery migration removes:

```text
OutcomeKinds.RunReady
OutcomeKinds.RunFailed
CompleteRunTransition
FailRunTransition
```

Tool derives Ready/Failed from `PipelineRunResult.Status`, not Delivery-specific
outcome strings. Ledger and dashboard may continue projecting host-level
`run.ready` and `run.failed` events.

## 4. State-Owned Interaction Behavior

No new interaction interface is needed. `PipelineNodes.WaitFor(...)` already binds
request creation and response application into a role-bearing interaction used by
composition and host registration.

Move only state-owned behavior:

```csharp
PipelineNodes.WaitFor<SupportState, CustomerQuestion, CustomerReply>(
    SupportIds.CustomerReply,
    state => state.CreateCustomerQuestion(),
    (state, reply) => state.RecordCustomerReply(reply))
```

Do not add an interaction hierarchy merely to move two adjacent delegates.

## Migration Inventory

State methods:

```text
SongwriterState.RecordSong
SongwriterState.RecordProofread
SupportState.RecordClassification
SupportState.RecordResolution
SupportState.CreateCustomerQuestion
SupportState.RecordCustomerReply
DebateState.RecordProposal
DebateState.RecordCritique
DebateState.RecordVerdict
DeliveryState.RecordPlannerDecision
DeliveryState.RecordReviewDecision
DeliveryState.RecordPlannerRequest
DeliveryState.RecordImplementationReport
DeliveryState.RecordCheckpoint
```

Output units:

```text
SongDecisionOutput.cs
ProofreaderDecisionOutput.cs
ClassificationDecisionOutput.cs
ResolutionDecisionOutput.cs
ProposalDecisionOutput.cs
CritiqueDecisionOutput.cs
PlannerDecisionOutput.cs
ReviewDecisionOutput.cs
```

Capability units:

```text
SubmitVerdictCapability.cs
AskPlannerCapability.cs
SubmitReportCapability.cs
WriteCheckpointCapability.cs
```

Delete hand-authored Planner and Reviewer JSON examples. Prompt builders retain
current application context, not duplicated response definitions.

## APIs That Remain Lightweight

Do not apply the definition pattern to:

- `PipelineNodes.WaitFor(...)`;
- `PipelineInteractionHandlers.Handle(...)`;
- `Agent.Create(...)`, `.WithMessage(...)`, session, and timeout options;
- `AgentDefinition.Success` and `.Failed` selectors;
- explicit `.Route(...)` calls;
- ordinary generated `[PipelineStage]` classes;
- `PipelineRunOptions`.

These already bind one concept directly or represent independently optional
concerns. More interfaces would add ceremony without removing convention coupling.

## Implementation Sequence

Begin only after durable ledger and gate work has landed and passed review.

1. Add output definitions and typed examples to Core.
2. Unify output serializer options and fresh-session example delivery.
3. Migrate Songwriter as the smallest complete proof.
4. Migrate Support, Debate, and state-owned interaction behavior.
5. Migrate Planner and Reviewer, including contextual validation and examples.
6. Add capability definitions and migrate Debate and Delivery capabilities.
7. Add terminal definitions and standard terminal adapters.
8. Remove Delivery custom terminal kinds and make Tool trust runner status.
9. Update README, authoring docs, package consumers, and exported API manifests.
10. Remove superseded overloads before v1 unless a concrete shipped consumer
    requires staged deprecation.

## Proof Requirements

Output proofs:

1. Instructions and field descriptions reach the provider schema.
2. Typed examples use the exact output serializer.
3. Invalid examples fail before invocation.
4. Fresh sessions receive examples once; retained sessions do not duplicate them.
5. Correction retains but does not resend examples.
6. Intrinsic and contextual validation run before Advanced acceptance.
7. Invalid or rejected output never applies state.
8. Corrected output applies state exactly once.

Capability proofs:

1. Definition metadata reaches the tool contract.
2. Validation precedes Advanced acceptance.
3. Invalid requests never accept or apply state.
4. Acceptance failure never commits state.
5. Accepted requests apply state exactly once.

Terminal proofs:

1. Generic state is inferred at the public call site.
2. Completion emits succeeded plus standard success.
3. Failure emits failed plus standard failure.
4. Definitions apply state exactly once and publish one completed observation.
5. Summaries contain no runtime source IDs or outcome-kind bookkeeping.
6. Tool, Ledger, and dashboard retain Ready/Failed behavior without custom
   pipeline outcome kinds.
7. Fault and cancellation paths remain runtime-owned.

Boundary proofs:

1. Packed Core consumers require no Advanced or MAF dependency.
2. Public signatures expose no provider, MAF, JSON DOM, session, or execution
   envelope types.
3. Samples remain unprivileged consumers.
4. `task check`, `git diff --check`, and Meridian pass.

## Non-Goals

- No global definition registry.
- No universal pipeline-definition hierarchy.
- No reflection-based validator or example discovery.
- No examples in arbitrary prompt JSON or provider-specific schema extensions.
- No state application inside output or capability definitions.
- No runtime bookkeeping in semantic values or `TState`.
- No asynchronous state transitions.
- No source-generated terminal ABI.
- No capability examples until a concrete need earns the surface.
- No interface conversion for routes, interactions, handlers, or ordinary stages.

## Worktree Constraint

Concurrent durable ledger and gate work currently overlaps the exact files this
campaign will change. Do not overwrite or revert that work. Re-read the stabilized
implementation before beginning, especially:

```text
src/Tandem/Authoring/AgentSdk.cs
src/Tandem/Authoring/AgentCapabilities.cs
src/Tandem/Authoring/PipelineStep.cs
src/Tandem/Domain/AgentBlockConfig.cs
src/Tandem/Infrastructure/Blocks/AgentBlock.cs
src/Tandem.Advanced/StructuredOutput.cs
src/Tandem.Advanced/AgentCapabilityAcceptance.cs
src/Tandem.Delivery/Capabilities/
src/Tandem.Delivery/Records/
src/Tandem.Delivery/DeliveryParticipantsFactory.cs
src/Tandem.Tool/Program.cs
tests/Tandem.Tests/Infrastructure/StructuredOutputTests.cs
tests/Tandem.Tests/Infrastructure/LocalCapabilityTests.cs
```
