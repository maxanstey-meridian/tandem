# Tandem Agent SDK Plan

## Decision

Keep Microsoft Agent Framework Harness as Tandem's maintained internal agent
engine. Replace the public `AgentBlock<TState>` and `AgentBlockConfig<TState>`
infrastructure surface with a small fluent Tandem agent SDK. Coding capabilities,
especially workspace access and mutation, become explicit opt-in capabilities
rather than requirements for every agent.

This is a greenfield replacement. Do not retain compatibility aliases,
constructors, adapters, or deprecated types.

## Why This Work Exists

Tandem already contains a substantial agent runtime, but the public authoring
experience still resembles infrastructure extracted from Delivery:

- consumers directly construct `AgentBlock<TState>` and
  `AgentBlockConfig<TState>`;
- every agent must provide a workspace path and mutation policy;
- Debate carries an artificial workspace solely to satisfy that contract;
- authored agents repeat delegation and result-envelope plumbing;
- consumer code imports `Tandem.Infrastructure.Blocks` and
  `Tandem.Infrastructure.Lifecycle`;
- public `AgentBlock<TState>` inherits a MAF `Executor`; and
- public `PipelineBuildContext` exposes MAF `AgentResponseUpdate`.

The runtime capability is sound. The public seam is not yet the clean agent SDK
implied by Tandem's typed pipeline API.

## Invariants

The implementation must preserve these established constraints:

1. The configured pipeline is the lifecycle. The runtime only executes it
   durably.
2. MAF remains the sole workflow, scheduling, durability, session, model-loop,
   and tool-dispatch substrate.
3. Tandem introduces no second agent loop, workflow engine, route model, or graph
   representation.
4. Authored steps remain plain partial classes with one `ExecuteAsync` method and
   one nested Dunet result union.
5. The source generator owns only step identity, the runtime adapter,
   `.Result.<Case>` selectors, and result adaptation.
6. The source generator does not infer prompts, models, sessions, tools, or agent
   behavior.
7. Agent behavior is never inferred from class names, `Agent` suffixes, step IDs,
   result names, profile names, or tool names.
8. Every agent explicitly supplies a userland session policy.
9. Profile and teardown policies remain optional, explicit, deterministic, and
   replay-safe.
10. Pipeline state contains durable facts, never services, clients, framework
    contexts, or a mutable state bag.
11. Delivery and external consumers remain unprivileged and import no MAF types
    or Tandem infrastructure namespaces.
12. No public Tandem signature or public base type exposes MAF.
13. Stable dependencies come from DI. Dynamic callbacks and observers remain
    isolated per pipeline build.
14. Lifecycle actions remain explicitly registered, validated, receipt-backed,
    replay-safe, and conflict-detecting.
15. Keep the implementation small and delegate commodity agent behavior to the
    maintained MAF SDK.

## What Tandem Keeps

The existing runtime behavior behind `AgentBlock<TState>` remains valuable and
must survive the API replacement:

- model profile selection;
- persisted agent sessions;
- session continuation, reset, and teardown;
- streaming updates;
- token accounting;
- structured-output validation and one corrective retry;
- grounding and acceptance policies;
- tool interception;
- lifecycle actions through MCP;
- receipt-backed replay and conflict detection;
- checkpointing;
- continuation turns;
- durable invocation identity; and
- replay-safe state transitions.

This machinery is what makes Tandem an agentic pipeline engine rather than a
workflow library with an incidental model call.

## Public Authoring Model

### Meridian application

This seam is earned: Tandem does not wrap MAF one-for-one. It adds durable
session policy, profile persistence, lifecycle-action replay, typed state
transitions, checkpoint ownership, and pipeline-scoped observation. Those are
Tandem semantics and form the public capability boundary.

Use `AgentRuntime` for the stable DI-owned capability that assembles configured
agent operations. This makes provenance explicit and avoids vague
service/provider naming. `AgentOperation<TState>` is the narrow operation an
authored step invokes. The MAF Harness implementation remains infrastructure.

Tests target observable execution, state, replay, durability, and architecture
boundaries. Do not test fluent methods through mock call choreography.

### Agent operation

Expose a plain Tandem-owned operation with no MAF inheritance or signatures:

```csharp
public sealed class AgentOperation<TState>
{
    public ValueTask<PipelineMessage<TState>> RunAsync(
        PipelineMessage<TState> pipeline,
        CancellationToken cancellationToken);
}
```

The concrete implementation may remain internal. The public type exists to give
authored agent steps one narrow operation to invoke.

### Fluent construction

Create agent operations through a stable DI-owned `AgentRuntime` and a small fluent
builder. The exact final names may change during implementation, but the shape
must remain:

```csharp
var classify = agentRuntime
    .Create<SupportState>(
        id: ClassifyAgent.StepId,
        profile: "support")
    .WithInstructions(SupportPrompts.Classify)
    .WithMessage(pipeline => pipeline.State.Ticket)
    .WithStructuredOutput(SupportPolicies.ParseClassification)
    .WithSessionPolicy(SupportPolicies.StartFresh)
    .Build(context);
```

The builder hides Tandem home paths, executable paths, chat-client construction,
MAF Harness options, persistence details, and internal runtime wiring.

Required configuration should be visible early:

- stable agent/step identity;
- configured model profile;
- system instructions;
- user-message projection; and
- explicit session policy.

Optional capabilities should be added deliberately:

```csharp
.WithStructuredOutput(...)
.WithContinuationPolicy(...)
.WithProfilePolicy(...)
.WithTeardownPolicy(...)
.WithLifecycleActions(...)
.WithCheckpoint(...)
.WithMessageAugmentation(...)
.WithWorkspace(...)
```

Do not create one omnipotent callback or one large positional options record.

### Authored agent step

An authored agent remains an ordinary generated pipeline step. It receives the
configured operation, invokes it, and maps semantic outcomes into its own Dunet
result cases:

```csharp
[PipelineStage(StepId)]
public sealed partial class ClassifyAgent(
    AgentOperation<SupportState> operation)
{
    public const string StepId = "classify";

    [Union]
    public partial record ClassifyResult
    {
        public partial record Categorized(
            SupportState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome);

        public partial record Unexpected(
            SupportState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome);
    }

    public async ValueTask<ClassifyResult> ExecuteAsync(
        PipelineMessage<SupportState> pipeline,
        CancellationToken cancellationToken)
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);

        return result.LatestOutcome?.Kind switch
        {
            "support.categorized" => new ClassifyResult.Categorized(
                result.State,
                result.Runtime,
                result.LatestOutcome),

            _ => new ClassifyResult.Unexpected(
                result.State,
                result.Runtime,
                result.LatestOutcome!),
        };
    }
}
```

This mapping remains userland because the owning pipeline defines what model and
lifecycle outcomes mean. Tandem must not move those semantics into the generator
or generic runtime.

No generic agent base class is required. Pipeline-specific helper inheritance
such as `DeliveryAgentStage` and `DebateAgentStage` should disappear unless a
concrete non-framework reuse case remains after the new operation is introduced.

## Workspace Capability

Workspace access must be absent by default.

MAF Harness already treats `FileAccessStore` as optional. A basic classifier,
support agent, or debate agent should construct no file store and receive no file
tools.

Delivery opts in explicitly:

```csharp
.WithWorkspace(
    path: state => state.WorkspacePath,
    mutationPolicy: DeliveryPolicies.RequirePlannerApproval)
```

The exact fluent grammar should make these states unambiguous:

- no workspace capability;
- read-only workspace capability; and
- workspace mutation governed by an explicit userland policy.

Do not silently grant mutation. Do not make non-coding agents invent a workspace
path.

## Model and Profile Boundary

`AddTandem()` continues to own stable chat-client and profile resolution
machinery. Pipeline registration owns which configured profile each agent uses.

`Microsoft.Extensions.AI.IChatClient` is an acceptable host/composition-root
abstraction. MAF workflow and agent implementation types are not acceptable
public Tandem contracts.

The factory should use the configured profile by default and preserve optional
userland profile-promotion policy. A selected profile must still be persisted
before the model call it governs.

## Session and Replay Boundary

Every built agent must supply an explicit session policy. The builder must reject
an incomplete definition before execution rather than relying only on a late
runtime failure.

The existing policy separation remains:

- session policy chooses continue, reset, or teardown before invocation;
- profile policy selects the profile for the invocation; and
- teardown policy releases session and usage bookkeeping after an accepted
  outcome.

Receipt replay must apply the same state, profile, session, and teardown effects
without repeating a model call or MCP process.

## Observation Boundary

`PipelineBuildContext` must stop exposing MAF `AgentResponseUpdate`.

Introduce the smallest Tandem-owned semantic update or observer contract needed
by hosts. Translate MAF update content internally into concepts such as:

- agent text;
- agent reasoning;
- usage;
- tool started; and
- tool completed.

The existing `RunEventProjector` already performs this semantic translation. Move
the translation behind the public boundary rather than mirroring MAF update
types.

Dynamic observation remains build-scoped so concurrent builds from one DI root
cannot capture each other's callbacks or observers.

## Namespace and Package Boundary

Keep one Tandem package for this change. Meridian's single-package default applies:
a new assembly is not justified by the
problem and would add dependency and release complexity without improving the
ownership boundary.

Expose intentional SDK namespaces such as:

```text
Tandem
Tandem.Agents
Tandem.Actions
```

The exact namespace split should minimize imports for the common author journey.
Consumers must no longer import:

```text
Tandem.Infrastructure.Blocks
Tandem.Infrastructure.Lifecycle
```

Runtime implementations remain internal under infrastructure namespaces.

Because this is greenfield, move or replace public types directly. Do not add
forwarders or aliases for the old namespace surface.

## Non-Goals

This work does not:

- add another workflow runtime;
- add another agent loop;
- make `[PipelineStage]` automatically call a model;
- add `[PipelineAgent]` magic;
- infer lifecycle policy from names or roles;
- teach the generator about prompts, models, tools, or sessions;
- add a generic agent base-class requirement;
- introduce a service locator or keyed lookup hidden inside authored steps;
- add a second graph or delayed compilation stage;
- preserve `AgentBlock<TState>` compatibility; or
- publish generic arbitrary agent tools without an explicit safety decision.

## Open Human Decision: Arbitrary Tools

Generic `.WithTools(...)` requires an explicit security and replay policy before
it becomes public API.

MAF invokes supplied tools without approval by default. Tandem lifecycle actions
are safer because they are validated, receipt-backed, replay-safe, and
conflict-detecting.

The initial agent SDK can remain complete and small with:

- optional Harness workspace tools;
- existing validated lifecycle actions; and
- deterministic pipeline steps for external-system operations.

Do not silently expose arbitrary tools. Before adding them, decide:

- which tools require approval;
- how side effects survive retries and replay;
- whether read-only and mutating tools need distinct contracts;
- how tool arguments and outputs are validated; and
- how tool identity is persisted.

## Implementation Plan

### Slice 0: Characterize the existing agent runtime

1. Keep all current agent, structured-output, lifecycle, session, and durable
   tests green.
2. Add focused characterization for no lifecycle call, structured success,
   structured failure and correction, continuation exhaustion, checkpointing,
   session continuation/reset/teardown, profile selection, receipt replay, and
   streaming observation.
3. Confirm which legacy public executor paths are unused. Remove them in the
   owning slice rather than carrying them into the new SDK.
4. Add a reflection-based public API test that discovers MAF types in exported
   signatures, generic constraints, properties, methods, constructors, and base
   classes. Text scans are insufficient.

Exit gate: behavior and current boundary leaks are pinned before structural
change.

### Slice 1: Establish clean public contracts

1. Add the Tandem-owned `AgentOperation<TState>` contract.
2. Add the Tandem-owned agent factory and minimum fluent builder.
3. Keep `AgentBlockConfig<TState>` or its replacement internal.
4. Remove MAF inheritance from the operation invoked by authored steps.
5. Replace public MAF update types with a Tandem-owned semantic observation
   contract.
6. Move public agent policy contracts into intentional SDK namespaces.
7. Ensure incomplete definitions fail during build/construction with actionable
   errors.

Exit gate: one agent can be configured and invoked without any public MAF or
infrastructure type.

### Slice 2: Make capabilities optional

1. Make workspace/file access absent by default.
2. Add explicit read-only and policy-governed mutation configuration.
3. Preserve optional structured output, acceptance, correction, continuation,
   profile, teardown, lifecycle-action, checkpoint, and augmentation behavior.
4. Keep lifecycle action-set identity explicit whenever lifecycle actions or
   checkpoint actions are configured.
5. Do not add arbitrary generic tools until the human decision above is resolved.

Exit gate: a basic agent executes with no workspace, while a Delivery-shaped
agent retains its existing guarded workspace behavior.

### Slice 3: Migrate the external consumer first

1. Migrate Debate to the new public agent SDK before Delivery.
2. Remove `WorkspacePath` from `DebateState`.
3. Remove `DebateAgentStage` delegation inheritance.
4. Remove all Debate imports of Tandem infrastructure namespaces.
5. Preserve its retained critic session, fresh judge session, lifecycle verdict
   action, replay behavior, conflict detection, in-process execution, and durable
   execution.
6. Add a small no-workspace support-classifier proof using the same public API.

Exit gate: two non-Delivery consumers prove the SDK without workspace or
privileged access.

### Slice 4: Migrate Delivery

1. Replace `DeliveryStepsFactory.CreateAgentBlock` with the fluent agent factory.
2. Keep agent identity, profile, prompts, user-message projection, structured
   output, acceptance, correction, lifecycle actions, checkpointing, mutation,
   continuation, session, and teardown explicit.
3. Make planner and reviewer workspace access explicitly read-only.
4. Make executor mutation explicitly governed by Delivery's planner-approval
   policy.
5. Remove `DeliveryAgentStage` delegation inheritance.
6. Preserve isolated concurrent pipeline builds and dynamic observation.

Exit gate: Delivery repeatedly expresses product policy rather than agent-runtime
construction and all correctness-ledger proofs remain green.

### Slice 5: Delete the old seam and document the real one

1. Remove `AgentBlock<TState>` and `AgentBlockConfig<TState>` from the public SDK;
   retain internal implementation only where its low-level characterization
   remains useful.
2. Delete confirmed-dead legacy MAF executor paths and their obsolete tests.
3. Remove stale public infrastructure types that exist only for the old seam.
4. Strengthen architecture checks so Delivery and consumers import no MAF or
   Tandem infrastructure namespaces.
5. Rewrite the README's coder and customer-support examples against the actual
   fluent agent API.
6. Update `docs/pipeline-authoring.md` with model/profile setup, agent definition,
   policies, optional capabilities, typed result mapping, registration, and
   durable execution.

Exit gate: documentation contains no fictional `WriteCodeAsync`, `ReviewAsync`,
or `ClassifyAsync` seam and can be followed directly against the public SDK.

## Required Proofs

The completed change must prove:

1. A basic agent runs without a workspace or file tools.
2. Delivery still receives file tools and enforces mutation authority.
3. Planner and reviewer remain read-only.
4. Every agent supplies an explicit session policy.
5. Structured output updates typed pipeline state.
6. Invalid structured output receives one correction and then fails closed.
7. Session continuation and reset survive pipeline loops.
8. Profile selection is persisted before model invocation.
9. Teardown and receipt transitions replay without another model call.
10. Accepted lifecycle actions remain validated, replay-safe, and
    conflict-detecting.
11. Concurrent pipeline builds isolate dynamic callbacks and observers.
12. Debate contains no artificial workspace state.
13. Delivery and external consumers import no MAF or Tandem infrastructure
    namespaces.
14. No exported Tandem signature, generic constraint, or base type references
    MAF.
15. Authored agent steps remain ordinary generated pipeline steps with nested
    Dunet results and `.Result.<Case>` routing.
16. In-process and durable closed-generic execution remain green.
17. The complete repository check passes with no warnings or formatting drift.

## Completion Standard

The change is complete when a first-time author can see, in one short example:

1. where the `IChatClient` or profile comes from;
2. where system instructions and the user message are configured;
3. which session policy the agent uses;
4. which capabilities the agent receives;
5. how model output changes typed state;
6. how that output becomes an authored Dunet result case; and
7. how the generated result selector routes to the next step.

That journey must use only public Tandem SDK types, must not mention MAF, and must
not require a workspace unless the pipeline actually operates on files.

## Implementation Status

Implemented:

- public `AgentRuntime`, fluent `AgentBuilder<TState>`, and plain
  `AgentOperation<TState>`;
- explicit model client, profile, instructions, message, and session policy;
- opt-in workspace capability with policy-governed mutation;
- structured output, lifecycle actions, checkpointing, continuation, profile,
  augmentation, and teardown options;
- Tandem-owned semantic streaming updates and observation contracts;
- no MAF types in exported Tandem signatures or public base classes;
- `Tandem.Actions` as the consumer-facing lifecycle-action namespace;
- Debate migrated without an artificial workspace or infrastructure imports;
- Delivery migrated with explicit workspace, mutation, checkpoint, and lifecycle
  policies;
- README and authoring documentation using the real model-backed seam; and
- behavior, architecture, in-process, replay, and durable proofs.

The former `AgentBlock<TState>` and positional config remain internal runtime
implementation details covered by existing low-level characterization tests.
They are no longer public SDK or consumer dependencies. Renaming those internals
would add churn without changing ownership or behavior and is not required for
the public replacement.

Deferred by explicit design:

- generic arbitrary `.WithTools(...)` remains unavailable until its approval,
  validation, side-effect, and replay policy is decided.
