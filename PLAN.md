# Tandem Pipeline Authoring

## Goal

**Make Tandem an installable pipeline-authoring and execution library whose
consumer code expresses lifecycle invariants, participants, and routes while
Tandem supplies commodity execution machinery and Microsoft Agent Framework
remains the only workflow graph and orchestration engine.**

The flagship pipeline is **Rig**. Rig plans, implements, verifies, and
independently reviews a bounded software change. It is a first-party consumer of
the same public Tandem API available to external packages; it has no privileged
access to Tandem internals.

The desired consumer experience is:

```csharp
services
    .AddTandem()
    .AddRig();
```

Or, for a consumer-owned pipeline:

```csharp
services
    .AddTandem()
    .AddReleaseReview();
```

Pipeline authors should repeatedly write only their durable facts, invariants,
prompts, policies, state transitions, lifecycle actions, participant-owned
outcomes, participants, and routes. They should not repeatedly wire MAF executor
bindings, chat-client construction, process execution, session persistence,
lifecycle receipts, MCP transport, replay handling, observation, or durable
execution.

## Product Model

There are three distinct product concepts:

- **Tandem** is the installable authoring and execution library.
- **Rig** is Tandem's batteries-included software delivery pipeline.
- A **custom pipeline** is a consumer-owned state, participant inventory, and
  workflow graph authored against Tandem's public API.

Rig is not a base class and is never extended. A consumer that wants different
behavior creates and registers a different pipeline. Reusable Rig operations may
be published later only when a concrete consumer proves that reuse is valuable.

## Hard Boundaries

1. MAF remains the only executable graph, scheduler, orchestration engine,
   durability mechanism, suspension mechanism, agent loop, and tool dispatcher.
2. Tandem must not add route descriptors, a route registry, a graph AST, delayed
   graph compilation, a pipeline DSL, or another workflow runtime.
3. Routes declared through Tandem authoring helpers must immediately register
   real MAF edges.
4. A pipeline owns its concrete `TState`, participant-owned outcomes, prompts,
   policies, lifecycle-action contracts, and state transitions.
5. Tandem owns composition-neutral runtime bookkeeping and execution machinery.
6. There is no universal pipeline-state interface and no state bag.
7. There is no Rig inheritance, pipeline inheritance, assembly scanning, magical
   discovery, or convention-based plugin loading.
8. Registration is explicit. A composition identity connects its workflow and
   lifecycle-action surface.
9. Use DI for stable machinery and explicit method parameters for dynamic build
   context. Do not use `IServiceProvider` or an internal service locator inside
   stages, agents, or compositions.
10. Invalid model or lifecycle-action output fails closed and cannot mutate
    pipeline state.
11. Accepted lifecycle actions remain receipt-backed, replay-safe,
    conflict-detecting, and mechanically terminate the active model turn.
12. This library is greenfield. Do not add migration code, aliases, shims, or
    compatibility support for old SimpleV1 identities or in-flight runs.
13. MAF is an internal execution substrate behind a Tandem anti-corruption
    layer. Public Tandem authoring contracts must not inherit from, accept, or
    return MAF types.
14. MAF orchestration history is runtime-owned and intentionally non-portable.
    Tandem-owned pipeline state remains plain durable data. Replacing MAF may
    start new runs on a new substrate; no historical-runtime migration is
    required while the library remains greenfield.

## Target Packages

Package boundaries reflect what a consumer independently installs, not Clean
Architecture layer names:

```text
Tandem
Tandem.Rig
Tandem.Tool
```

Do not create `Tandem.Abstractions`, `Tandem.Runtime`, or public `Domain`,
`Application`, and `Infrastructure` packages. Those names expose internal
architecture rather than consumer capabilities.

### Tandem

The public authoring API and reusable implementation:

- `PipelineMessage<TState>` and `PipelineRuntime`;
- participant-owned typed outcomes, internal outcome adaptation, and agent usage
  bookkeeping;
- Tandem participant base types and the internal MAF participant adapter;
- `AgentBlock<TState, TOutcome>` and its configuration/policy contracts;
- an opaque Tandem `Pipeline` handle over the active execution substrate;
- lifecycle receipt persistence, replay, and conflict handling;
- composition-supplied lifecycle-action registration machinery;
- MAF workflow hosting and durable execution support;
- generic observation and projection machinery;
- commodity services such as command execution when reuse is proven.

### Tandem.Rig

The first-party production pipeline:

- Rig state, participant-owned outcomes, participant IDs, and workflow graph;
- Rig stages and agents;
- Rig prompts, policies, and structured decisions;
- Rig lifecycle actions and their registration;
- Rig-specific Git, verification, human-decision, and run-state semantics.

`Tandem.Rig` references only public Tandem APIs. Tandem does not reference Rig.

### Tandem.Tool

The executable/operator surface:

- CLI commands and configuration loading;
- composition selection and process startup;
- dashboard and terminal rendering;
- application-host wiring.

Physical extraction of `Tandem.Tool` may follow Rig extraction if doing both in
one slice obscures the authoring boundary. `Program.cs` may remain hard-coded to
Rig during this plan, but new shared runtime code must not deepen that coupling.

## Public Vocabulary

Use capability-oriented namespaces:

```csharp
using Tandem;
using Tandem.Agents;
using Tandem.Lifecycle;
```

Use a predictable authoring grammar. The semantic prefix changes; the suffix
states the component's pipeline role.

| Suffix | Meaning | Examples |
| --- | --- | --- |
| `Agent` | Model-backed participant | `PlannerAgent`, `DebaterAgent` |
| `Stage` | Deterministic participant | `VerificationStage`, `LintStage` |
| `Port` | Durable request/response boundary | `HumanInputPort` |
| `Action` | Validated model-invoked lifecycle action | `SubmitReportAction` |
| `Policies` | Named behavioral invariants | `ExecutorPolicies` |
| `Prompts` | Instructions and message projections | `ReviewerPrompts` |
| `Decision` | Structured model result | `PlannerDecision` |
| `Composition` | MAF graph declaration | `RigComposition` |
| `Participants` | Typed graph-participant inventory | `RigParticipants` |
| `State` | Durable pipeline-owned facts | `RigState` |
| `Registration` | Explicit DI/package registration | `RigRegistration` |

Avoid vague suffixes such as `Manager`, `Service`, `Processor`, `Handler`,
`Provider`, and `Helper` unless they identify a genuinely distinct role.

## Target Rig Layout

```text
src/Tandem.Rig/
|-- RigComposition.cs
|-- RigParticipants.cs
|-- RigState.cs
|-- RigStageIds.cs
|-- RigRegistration.cs
|-- Agents/
|   |-- Executor/
|   |   |-- ExecutorAgent.cs
|   |   |-- ExecutorPrompts.cs
|   |   `-- ExecutorPolicies.cs
|   |-- Planner/
|   |   |-- PlannerAgent.cs
|   |   |-- PlannerPrompts.cs
|   |   |-- PlannerPolicies.cs
|   |   `-- PlannerDecision.cs
|   `-- Reviewer/
|       |-- ReviewerAgent.cs
|       |-- ReviewerPrompts.cs
|       |-- ReviewerPolicies.cs
|       `-- ReviewerDecision.cs
|-- Stages/
|   |-- PrepareWorkspaceStage.cs
|   |-- CaptureCandidateStage.cs
|   |-- VerificationStage.cs
|   |-- HumanDecisionStage.cs
|   |-- CompleteRunStage.cs
|   `-- FailRunStage.cs
`-- Actions/
    |-- AskPlannerAction.cs
    |-- SubmitReportAction.cs
    |-- WriteCheckpointAction.cs
    `-- RigLifecycleActions.cs
```

This is a growth pattern, not mandatory empty scaffolding. Small agents may need
only `<Name>Agent.cs` and `<Name>Prompts.cs`. Tightly coupled contracts and
validators may share the action or decision file that owns them.

## Participant Authoring Surface

Userland works with semantic participants and their closed outcome protocols, not
MAF `ExecutorBinding` values or pipeline-wide string outcomes.

The desired inventory is a DI-constructible positional record:

```csharp
internal sealed record RigParticipants(
    PrepareWorkspaceStage PrepareWorkspace,
    ExecutorAgent Executor,
    PlannerAgent Planner,
    CaptureCandidateStage CaptureCandidate,
    VerificationStage Verification,
    ReviewerAgent Reviewer,
    HumanDecisionStage HumanDecision,
    CompleteRunStage CompleteRun,
    FailRunStage FailRun
);
```

There is no `CreateNodes`, constructor body, duplicated property list,
`BindExecutor`, `ExecutorBinding`, or factory conversion in pipeline code.

### Tandem participant adapter

Tandem supplies public participant contracts with no MAF inheritance and adapts
them to MAF internally:

```csharp
public interface IPipelineParticipant
{
    string Id { get; }
}

public interface IPipelineParticipant<TOutcome> : IPipelineParticipant
    where TOutcome : struct, Enum;

public abstract class PipelineStage<TInput, TOutput, TOutcome>(string id)
    : IPipelineParticipant<TOutcome>
    where TOutcome : struct, Enum
{
    public string Id { get; } = id;

    public abstract ValueTask<ParticipantResult<TOutput, TOutcome>> ExecuteAsync(
        TInput input,
        CancellationToken cancellationToken
    );
}
```

The first public contract should omit an execution context unless a real
consumer requires one. When workflow capabilities are required, expose the
smallest Tandem-owned capability contract, not MAF `IWorkflowContext` and not a
one-for-one mirror of it. Human suspension and durable state access should use
focused Tandem capabilities or participants.

An internal adapter owns MAF inheritance:

```csharp
internal sealed class MafStageExecutor<TInput, TOutput, TOutcome>(
    PipelineStage<TInput, TOutput, TOutcome> stage
) : Executor<TInput, TOutput>(stage.Id)
    where TOutcome : struct, Enum
{
    public override ValueTask<TOutput> HandleAsync(
        TInput input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    ) => AdaptResultAsync(stage.ExecuteAsync(input, cancellationToken));
}
```

The adapter maps the participant-owned typed outcome to MAF's runtime outcome
representation without exposing that representation to userland.

`AgentBlock<TState, TOutcome>` is also a typed pipeline participant. A custom
deterministic stage contains only its operation, typed state transition, and
closed outcome protocol:

```csharp
internal sealed class LintStage(...)
    : PipelineStage<
        PipelineMessage<MyState>,
        PipelineMessage<MyState>,
        LintStage.Outcome
    >("lint")
{
    public enum Outcome
    {
        Passed,
        Failed,
    }

    public override async ValueTask<
        ParticipantResult<PipelineMessage<MyState>, Outcome>
    > ExecuteAsync(...);
}
```

Outcomes live with the participant that can emit them. Composition refers to
`LintStage.Outcome.Passed`, `PlannerAgent.Outcome.Approved`, and equivalent
participant-owned values. This is truthful coupling: composition already depends
on the concrete participant through `from`, and the nested enum makes that
participant's routing protocol explicit and discoverable.

Do not provide universal default `Passed`/`Failed` outcomes or an open shared
outcome value. Default interface methods cannot provide an extensible enum, and a
single open outcome type would allow a planner outcome to be routed from a
verification stage. A shared enum such as `PassFailOutcome` is valid only when
multiple participants deliberately implement the same complete semantic
protocol. Similar words alone do not justify sharing a type.

The exact internal binding implementation requires a MAF lifecycle spike. The
safe default is one fresh binding per participant per workflow build, reused for
every visit to that participant within the built graph. Binding lifetime is not
a userland option. Cache bindings across workflow builds only if tests prove MAF
defines them as reusable and concurrent-safe. This may change Tandem internals
but must not change the public participant syntax.

Binding lifetime is distinct from agent-session lifetime:

- Tandem owns build-scoped MAF binding creation;
- DI/hosting owns participant-instance lifetime;
- userland policy owns conversation/session continuation, reset, promotion, and
  teardown decisions.

## Composition Syntax

`<Name>Composition.cs` is the complete route map. It receives the typed
participant inventory and contains no participant construction or infrastructure
wiring. Composition uses one fluent Tandem builder whose route calls immediately
register real MAF edges:

```csharp
public sealed class RigComposition(RigParticipants participants)
{
    public Pipeline Build()
    {
        return TandemWorkflow
            .Start(
                at: participants.PrepareWorkspace,
                name: "rig",
                description:
                    "Plan, implement, verify, and independently review a software change."
            )
            .Route(
                on: PrepareWorkspaceStage.Outcome.Prepared,
                from: participants.PrepareWorkspace,
                to: participants.Executor,
                label: "workspace prepared"
            )
            .Route(
                when: IsUnexpectedPrepareOutcome,
                from: participants.PrepareWorkspace,
                to: participants.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: CaptureCandidateStage.Outcome.Captured,
                when: pipeline => pipeline.State.Packet.Verification.Count > 0,
                from: participants.CaptureCandidate,
                to: participants.Verification,
                label: "verification configured"
            )
            .Route(
                from: participants.HumanDecision.Request,
                to: participants.HumanDecision.Port,
                label: "request human input"
            )
            .Route(
                from: participants.HumanDecision.Port,
                to: participants.HumanDecision.ApplyResponse,
                label: "answer received"
            )
            .Build(
                outputs: [participants.CompleteRun, participants.FailRun]
            );
    }
}
```

The graph uses one `Route` operation with strongly typed overloads:

```csharp
Route(from:, to:, label:);
Route(on:, from:, to:, label:);
Route(when:, from:, to:, label:);
Route(on:, when:, from:, to:, label:);
```

`on:` identifies a discrete participant-owned outcome. `when:` supplies a
Boolean pipeline predicate. Do not call an outcome `when`, and do not hide a
participant's meaningful transition behind a default outcome.

All non-obvious arguments use named parameters. Conditional route calls lead with
`on:` or `when:` so the reason for the route is visible first, followed by
`from:`, `to:`, and `label:`. Unconditional routes lead with `from:`. Predicate
parameters use `pipeline` and receive `PipelineMessage<RigState>`:

```csharp
pipeline.State
pipeline.Runtime
pipeline.LatestOutcome
```

The generic `from` and `on` parameters share the same `TOutcome`, so C# rejects a
route from `VerificationStage` on `PlannerAgent.Outcome.Approved`. This
compatibility is enforced at compile time, not deferred to graph construction.

MAF `Workflow`, `WorkflowBuilder`, `IWorkflowContext`, `Executor`,
`ExecutorBinding`, and request-port types do not appear in public authoring
signatures or route-authoring code. Tandem's opaque `Pipeline` contains the built
runtime workflow but no duplicate graph representation.

### Route-helper boundary

The fluent Tandem builder is a zero-storage adapter proven by Rig and the
consumer sample. Each `Route` overload must:

1. Accept semantic `IPipelineParticipant` values.
2. Resolve MAF bindings internally.
3. Immediately call the corresponding MAF `AddEdge` overload.
4. Store no route definition and compile no later graph.
5. Return the same Tandem builder for chaining; `Build` returns the opaque Tandem
   `Pipeline`.

Do not add `RouteDefinition`, `RouteMap`, `RouteRegistry`, a Tandem graph model,
or staged `.From(...).On(...).To(...)` grammar. The latter requires temporary
route state and drifts toward delayed graph compilation. The implementation spike
should select the smallest fluent surface that preserves immediate registration
without exposing MAF binding machinery.

MAF evaluates outgoing conditions independently; route declaration order must not
pretend to provide `if/else` semantics. Unexpected-outcome predicates remain
explicit complements, named by behavior:

```csharp
IsUnexpectedPlannerOutcome
IsUnexpectedVerificationOutcome
```

Do not add `RouteOtherwise` unless MAF itself supplies that semantic guarantee.

## Agent Pattern

Each substantial model-backed role owns a predictable optional file set:

```text
Agents/<Name>/
|-- <Name>Agent.cs
|-- <Name>Prompts.cs
|-- <Name>Policies.cs
`-- <Name>Decision.cs
```

`<Name>Agent.cs` assembles `AgentBlock<TState, TOutcome>` from framework machinery
supplied by DI and invariant-bearing functions supplied by the pipeline. Each
agent owns a nested `Outcome` enum just like a deterministic stage.

Call sites use behavioral names:

```csharp
mutationPolicy: ExecutorPolicies.RequirePlannerApprovalForWrites,
continuationPolicy: ExecutorPolicies.RequireExecutorLifecycleOutcome,
sessionPolicy: ExecutorPolicies.ContinueWorkingSession,
profilePolicy: ExecutorPolicies.PromoteWhenContextDemands,
teardownPolicy: ExecutorPolicies.ReleaseSessionAfterAcceptedReport,
receiptTransition: ExecutorPolicies.ApplyExecutorReceipt
```

Do not use construction-oriented names such as `CreateMutationGate` or
`CreateExecutorTurnPolicy` when the value represents a business rule.
`BuildUserMessage` remains appropriate because it actually builds a message.

`<Name>Prompts.cs` owns system instructions, user-message projection, checkpoint
instructions, and role-specific correction text.

`<Name>Policies.cs` owns named behavioral invariants, structured-output mapping,
acceptance rules, continuation rules, mutation interception, and receipt state
transitions.

`<Name>Decision.cs` owns tightly coupled structured output contracts and semantic
validation for that role.

### Agent lifecycle policies

Agent conversation lifecycle is a real userland invariant. Tandem supplies typed
policy contracts, decision values, durable execution, and replay-safe
application. Rig or another consumer pipeline supplies the decisions.

Every agent explicitly supplies its session policy; Tandem does not silently
choose retention or reset. Profile-promotion and teardown policies are optional:
absence means use the agent's configured profile and perform no additional
post-outcome teardown. It does not authorize Tandem to infer behavior from the
agent's name or role.

Keep the concerns explicit rather than introducing one omnipotent lifecycle
callback:

- a session policy decides whether to continue, reset, or tear down a session;
- a profile policy selects or promotes the model profile for the next invocation;
- a teardown policy decides what runtime bookkeeping is released after an
  accepted outcome.

Representative Tandem-owned decision values may include:

```csharp
public enum AgentSessionAction
{
    Continue,
    Reset,
    Teardown,
}

public sealed record AgentProfileDecision(
    string ProfileName,
    string Reason
);
```

Representative userland policies include:

```csharp
ExecutorPolicies.ContinueWorkingSession
ExecutorPolicies.PromoteAfterRepeatedFailure
ExecutorPolicies.ReleaseSessionAfterAcceptedReport
ReviewerPolicies.StartFreshForEachCandidate
ReviewerPolicies.TeardownAfterDecision
CriticPolicies.RetainRevisionContext
```

Tandem must not infer lifecycle behavior from participant IDs, tool names, or Rig
roles. Do not add checks such as `participant.Id == "reviewer"` or generic
failure-count promotion defaults.

Policy execution must preserve durability and replay invariants:

1. Decisions are deterministic from persisted `PipelineMessage<TState>` facts.
2. A selected profile is persisted before the model call it governs.
3. Session reset, rotation, or teardown is committed atomically with the
   resulting pipeline message.
4. Receipt replay applies the same lifecycle transition without repeating the
   model call.
5. Policies return decisions; they do not perform hidden external side effects.

A richer pipeline may retain an executor or critic conversation across loops,
start a final reviewer fresh for each candidate, promote after repeated failures,
rotate after checkpointing, or tear down after lifecycle-action acceptance. Those
are pipeline policies, not Tandem defaults and not MAF binding lifetimes.

## Stage Pattern

Each deterministic graph participant is named `<Name>Stage` and normally lives
in `Stages/<Name>Stage.cs`.

A stage:

1. Receives a typed pipeline message or another explicit declared input type.
2. Performs one operation.
3. Returns new typed state and one outcome.
4. Never selects its successor.
5. Declares machinery through constructor dependencies resolved by DI.
6. Contains only composition meaning and invariants after commodity mechanics are
   delegated to Tandem services.

Examples:

```text
PrepareWorkspaceStage
CaptureCandidateStage
VerificationStage
LintStage
HumanDecisionStage
CompleteRunStage
FailRunStage
```

Repeated semantic names across independent pipelines are acceptable and useful.
Do not promote `VerificationStage` or `HumanDecisionStage` into Tandem merely
because several pipelines may have similarly named files. Extract only proven
composition-neutral mechanics, such as command execution or typed request-port
persistence, while keeping state interpretation and lifecycle outcomes owned by
the pipeline.

## Lifecycle Action Pattern

Validated model-invoked lifecycle actions use `<Verb><Noun>Action`:

```text
AskPlannerAction
SubmitReportAction
WriteCheckpointAction
SubmitVerdictAction
```

Each action file owns its request contract, validator, tool metadata, handler,
receipt kind and payload, replay behavior, and conflict behavior when those
pieces are tightly coupled. `Action` describes what the model invokes without
implying that the pipeline ends; accepted actions mechanically terminate only the
active model turn and return control to routing.

Rig explicitly registers its lifecycle-action set:

```csharp
internal static class RigLifecycleActions
{
    public const string Name = "rig";

    public static IMcpServerBuilder Register(IServiceCollection services) =>
        services
            .AddMcpServer()
            .WithTools<AskPlannerAction>()
            .WithTools<SubmitReportAction>()
            .WithTools<WriteCheckpointAction>();
}
```

Remove the generic `AgentBlockConfig<TState, TOutcome>` default MCP identity. A
participant with lifecycle actions must select its action registration
explicitly.

The current shared-host `RunSimpleV1Async`/`RunDebateAsync` switch is not the final
extension surface. Replace it with the minimum explicit registration mechanism
needed for Rig and the consumer proof. Do not use assembly discovery. `Program.cs`
selection may remain hard-coded during this plan.

## DI Pattern

`AddTandem` registers stable commodity machinery once. Expected responsibilities
include chat-client creation, lifecycle receipt storage, MCP client creation,
agent session persistence, workspace/process machinery, observation, and MAF
hosting where those services are composition-neutral.

`AddRig` registers Rig participants, the typed participant inventory, its
composition, and its lifecycle-action set:

```csharp
services.AddTransient<PrepareWorkspaceStage>();
services.AddTransient<ExecutorAgent>();
services.AddTransient<PlannerAgent>();
services.AddTransient<CaptureCandidateStage>();
services.AddTransient<VerificationStage>();
services.AddTransient<ReviewerAgent>();
services.AddTransient<HumanDecisionStage>();
services.AddTransient<CompleteRunStage>();
services.AddTransient<FailRunStage>();
services.AddTransient<RigParticipants>();
services.AddTransient<RigComposition>();
```

The exact registrations belong only in `RigRegistration.cs`; they do not appear
in the graph.

Dynamic values such as per-build observers or update callbacks use an explicit
`PipelineBuildContext` or equivalent method parameter. They must not be pulled
from a service locator.

DI hides machinery, not invariants. Pipeline-specific decisions remain explicit
in agent policies, stage code, lifecycle-action contracts, state, and routes.

## Consumer Authoring Pattern

A consumer package follows the same shape outside Tandem's source tree:

```text
Acme.ReleaseReview/
|-- ReleaseReviewComposition.cs
|-- ReleaseReviewParticipants.cs
|-- ReleaseReviewState.cs
|-- ReleaseReviewStageIds.cs
|-- ReleaseReviewRegistration.cs
|-- Agents/
|-- Stages/
`-- Actions/
```

Start-to-finish recipe:

1. Reference `Tandem`.
2. Define one serializable `<Name>State` containing durable lifecycle facts.
3. Implement deterministic `<Name>Stage` participants with complete nested
   outcome enums.
4. Assemble model-backed `<Name>Agent` participants with prompts, policies, and
   complete nested outcome enums.
5. Share an outcome type only where multiple participants intentionally implement
   one complete semantic protocol.
6. Implement composition-owned lifecycle actions.
7. Declare the positional `<Name>Participants` inventory.
8. Declare the complete route map in `<Name>Composition`.
9. Register services, composition identity, and lifecycle actions in
   `<Name>Registration`.
10. Prove graph shape, state serialization, execution, replay, conflict handling,
    and durability.

Add `docs/pipeline-authoring.md` containing this recipe, a minimal example, and
links to Rig as the complete production example.

## External Consumer Proof

Graduate Debate from an internal test fixture into a separate consumer-style
sample/test project:

```text
samples/Tandem.Sample.Debate/
|-- Tandem.Sample.Debate.csproj
|-- DebateComposition.cs
|-- DebateParticipants.cs
|-- DebateState.cs
|-- DebateRegistration.cs
|-- Agents/
|-- Stages/
`-- Actions/
```

It references only `Tandem`. It must not use:

- `InternalsVisibleTo`;
- source inclusion;
- reflection into Tandem;
- Rig state, outcomes, stage IDs, policies, prompts, or lifecycle actions;
- edits to Tandem runtime to add Debate-specific switches.

The proof must exercise:

- `PipelineMessage<DebateState>`;
- at least two deterministic/model-backed participant categories;
- structured JSON state transition;
- a revision loop;
- a Debate-owned lifecycle action;
- receipt replay without a repeated model call;
- conflict detection;
- runtime session, usage, and invocation bookkeeping;
- graph reflection containing only Debate participants and routes;
- full state serialization;
- real in-process MAF execution;
- real durable closed-generic MAF execution.

## Graph Inspection and Export

Every Tandem composition publicly produces an opaque Tandem `Pipeline` containing
the real MAF `Workflow` internally. MAF already exposes that executable graph to
the Tandem adapter through:

```csharp
workflow.ReflectExecutors();
workflow.ReflectPorts();
workflow.ReflectEdges();
WorkflowVisualizer.ToMermaidString(workflow);
WorkflowVisualizer.ToDotString(workflow);
```

Tandem should expose this existing capability as pipeline inspection, not build a
renderer or maintain a second graph representation. The graph MAF executes must
be the graph Tandem describes and exports.

The authoring library should provide a small inspection result over the real
workflow containing stable semantic information such as:

- composition name and description;
- start participant;
- participant IDs;
- request ports and their input/output types;
- routes and whether they are conditional;
- terminal output participants;
- Mermaid output;
- Graphviz DOT output.

Do not parse Mermaid or DOT back into a Tandem model. Reflection supplies
structured inspection; Mermaid and DOT are export formats only. Treat MAF's full
rendered formatting as framework-owned and potentially unstable. Tandem tests
should pin semantic IDs, topology, labels, and valid format markers rather than
snapshot every character of generated output.

The eventual operator surface should support commands equivalent to:

```text
tandem graph rig
tandem graph rig --format mermaid
tandem graph rig --format dot
tandem graph rig --output rig.mmd
tandem describe rig
```

Exact CLI syntax may be selected when `Tandem.Tool` composition selection is
implemented. The library-level inspection/export capability must not depend on
the CLI and should work for external pipeline packages.

Acceptance tests must prove:

1. Inspection uses the built MAF `Workflow`, not route declarations retained by
   Tandem.
2. Every reflected route endpoint resolves to a reflected participant or port.
3. Route labels appear in Mermaid and DOT output where MAF supports them.
4. Mermaid output starts with a valid flowchart declaration and includes the
   start participant.
5. DOT output starts with a valid directed-graph declaration.
6. Rig and the external Debate sample can both be inspected without
   composition-specific inspection code.
7. Exported diagrams change when and only when the executable MAF graph changes.

## MAF Replacement Boundary

MAF is deliberately replaceable infrastructure, not part of Tandem's public
authoring model. This boundary does not make runtime replacement free; it confines
replacement cost to Tandem.

Portable Tandem and pipeline-owned concepts include:

- typed state, runtime facts, outcomes, and state transitions;
- participants and their declared input/output contracts;
- prompts, decisions, validators, and policies;
- lifecycle-action contracts and receipt semantics;
- route intent expressed through Tandem authoring operations;
- inspection data exposed from the opaque Tandem `Pipeline`.

Substrate-owned adapters include:

- participant-to-executor adaptation and binding;
- graph construction and workflow hosting;
- suspension and external resumption;
- durable orchestration history;
- workflow event projection;
- runtime graph reflection and visualization;
- agent-loop and tool-dispatch integration where MAF owns those mechanics.

A future MAF replacement rewrites those adapters and starts new runs on the new
substrate. It must not require rewriting Rig or external pipeline state, stages,
agents, policies, lifecycle actions, or route declarations. Historical MAF
orchestration instances are not a Tandem portability contract.

## Mechanical Enforcement

Add tests or project-level architecture checks proving:

1. Tandem does not reference `Tandem.Rig`.
2. `Tandem.Rig` references only public Tandem APIs.
3. The consumer sample references Tandem and not Rig.
4. Rig and consumer projects have no direct MAF package reference or MAF
   namespace import.
5. Generic Tandem code contains no Rig participant IDs, outcomes, prompts,
   lifecycle-action names, or state assumptions.
6. No generic MCP configuration defaults to Rig.
7. Pipeline state contains no services, framework contexts, or mutable state bag.
8. Every composition has graph-reflection and closed-generic serialization tests.
9. Every lifecycle action has validation, replay, and conflict tests.
10. Route helpers register MAF edges immediately and retain no parallel route
   representation.
11. Rig preserves its characterized topology and behavioral tests through the
    refactor unless this plan explicitly changes an identity.
12. Graph inspection and Mermaid/DOT export operate on the built MAF workflow and
    introduce no retained Tandem graph model.
13. Public Tandem and consumer-package signatures contain no MAF types, including
    `Executor`, `ExecutorBinding`, `IWorkflowContext`, `Workflow`,
    `WorkflowBuilder`, and MAF request-port types.
14. Every agent supplies an explicit userland session policy; profile promotion
    and teardown are absent or supplied by userland, never inferred by Tandem.
15. Agent session, profile, promotion, and teardown decisions are supplied by the
    owning pipeline and are not inferred by generic Tandem code.
16. Profile selection is persisted before model invocation, and session reset or
    teardown survives receipt replay without repeating the model call.
17. Every participant exposes a closed typed outcome protocol, and route overloads
    reject outcomes owned by a different participant protocol at compile time.
18. Participant-specific outcomes are nested with their participant; shared
    outcome types are used only for deliberately shared semantic protocols.
19. Every fluent `Route` call immediately registers a real MAF edge, returns the
    same builder, and retains no staged or parallel route representation.

## Execution Plan

### Slice 0: Characterize and spike MAF binding lifecycle

Before changing the public authoring surface:

1. Keep the existing 157-test green baseline.
2. Add focused tests for binding the same executor into one and multiple workflow
   builds.
3. Determine whether `ExecutorBinding` may be cached, must be fresh per workflow,
   or carries workflow-specific identity.
4. Test concurrent workflow builds and repeated visits to one participant within
   a loop.
5. Default to fresh per-build bindings unless reuse is proven safe.
6. Prove a plain Tandem participant with no public MAF inheritance can be wrapped
   by an internal MAF executor while preserving type validation, graph reflection,
   observation decoration, request ports, and durable identity.
7. Record the result in tests and use it to select the internal participant
   adapter implementation.

Exit gate: the desired participant syntax is supported by proven MAF behavior,
not assumptions.

### Slice 1: Introduce Tandem participants

1. Add the smallest public `IPipelineParticipant` and participant base type(s).
2. Ensure no public participant type inherits MAF `Executor` or accepts
   `IWorkflowContext`.
3. Adapt deterministic stages and `AgentBlock<TState, TOutcome>` through internal
   MAF executors without changing behavior.
4. Add the opaque Tandem `Pipeline`, `TandemWorkflow.Start`, and the minimum
   immediate MAF route adapter needed to accept semantic participants.
5. Keep MAF workflows, builders, request ports, and bindings internal to Tandem
   authoring machinery.
6. Prove heterogeneous participant types, request ports, observation decoration,
   graph reflection, and durable execution.
7. Add `IPipelineParticipant<TOutcome>`, the typed participant-result contract,
   and `Route` overloads that bind `from` and `on` to the same outcome type.

Exit gate: public Tandem, Rig, and composition code contains no MAF types,
`ExecutorBinding`, or `BindExecutor`.

### Slice 2: Rename SimpleV1 to Rig

1. Rename production SimpleV1 types, namespaces, workflow metadata, tests, and
   documentation to Rig.
2. Rename construction-oriented policy methods to behavioral names.
3. Remove dead SimpleV1 concepts and the obsolete waiting stage if graph
   characterization confirms they are unused.
4. Preserve the exact characterized 26-edge lifecycle shape and behavior.
5. Because the project is greenfield, do not retain SimpleV1 aliases or old
   workflow identities.

Exit gate: no production or test code refers to SimpleV1.

### Slice 3: Extract Tandem.Rig

1. Create `Tandem.Rig.csproj` referencing Tandem.
2. Move Rig state, participant-owned outcomes, IDs, stages, agents, prompts,
   policies, decisions, lifecycle actions, composition, and registration into it.
3. Promote only the Tandem APIs required by Rig to public authoring contracts.
4. Do not use `InternalsVisibleTo`.
5. Add project-reference checks enforcing `Tandem ->/ Rig` and `Rig -> Tandem`.

Exit gate: Rig builds and executes as an unprivileged package consumer.

### Slice 4: Apply the cookie-cutter Rig layout

1. Add `RigParticipants` as the positional DI-constructed inventory.
2. Delete `CreateNodes` and all userland binding ceremony.
3. Reduce `RigComposition` to workflow metadata, fluent route declarations,
   terminal graph outputs, and short graph predicates.
4. Use the strongly typed fluent `Route` overloads with named `from`, `on`,
   `when`, `to`, and `label` parameters.
5. Split agent assembly, prompts, policies, and decisions by the agreed grammar.
6. Split deterministic participants into `<Name>Stage.cs` files.
7. Split lifecycle tools into `<Verb><Noun>Action.cs` files.
8. Move Rig routing outcomes into closed enums nested with their owning
   participants; delete the pipeline-wide outcome catalog.

Exit gate: a reader can open `RigParticipants.cs` to see what exists and
`RigComposition.cs` to see exactly how it flows.

### Slice 5: Make machinery DI-provided

1. Add `AddTandem` registrations for composition-neutral machinery.
2. Add `AddRig` registrations for Rig participants, inventory, composition, and
   lifecycle-action set.
3. Constructor-inject stable dependencies into stages and agents.
4. Pass dynamic build observation through an explicit build context.
5. Remove service resolution and repeated machinery construction from Rig.
6. Remove the default SimpleV1/Rig MCP identity from generic agent config.
7. Add Tandem-owned session, profile-selection, and teardown policy contracts and
   decision values.
8. Move all executor/reviewer/critic retention, reset, promotion, and teardown
   behavior into named Rig policies.
9. Persist profile decisions before model calls and apply session lifecycle
   transitions atomically and replay-safely.

Exit gate: Rig code repeatedly expresses invariants, not framework plumbing.

### Slice 6: Explicit lifecycle-action registration boundary

1. Replace shared `RunSimpleV1Async`/`RunDebateAsync` methods with the minimum
   explicit registration-by-identity seam.
2. Keep registration deterministic and explicit; do not scan assemblies.
3. Ensure each agent with lifecycle tools selects its composition action set.
4. Preserve receipt replay, conflict detection, validation, and mechanical turn
   termination.
5. Leave final CLI composition selection hard-coded if necessary.

Exit gate: adding a consumer lifecycle-action set does not require a
consumer-specific switch in Tandem runtime.

### Slice 7: External Debate consumer proof

1. Create the separate Debate sample/test project referencing only Tandem.
2. Apply the same state, participant, agent, stage, lifecycle-action, composition,
   and registration grammar.
3. Port the existing deterministic Debate behavior and strengthen it with
   registration and conflict proofs.
4. Run in-process and durable closed-generic workflows.
5. Give Debate at least two distinct userland lifecycle policies, such as a
   retained critic session and a fresh or torn-down judge session, and prove the
   decisions survive loops and receipt replay.

Exit gate: Debate requires no Rig vocabulary, privileged access, or Tandem source
change.

### Slice 8: Documentation and enforcement

1. Add `docs/pipeline-authoring.md` with the complete author journey.
2. Update README positioning: Tandem is the engine; Rig is the flagship pipeline.
3. Update CONTRIBUTING boundaries and naming grammar.
4. Add library-level workflow inspection with Mermaid and DOT export over the
   built MAF workflow.
5. Add Rig and external Debate graph-inspection tests, preserving semantic graph
   assertions without brittle full-render snapshots.
6. Document the future `tandem graph` and `tandem describe` operator surface
   without coupling the library implementation to current CLI wiring.
7. Add architecture checks and project-reference tests.
8. Run formatting, analyzers, all tests, build, durable proofs, diff checks, and
   the repository architecture checker.

Exit gate: documentation, project structure, public API, Rig, and Debate all teach
the same authoring pattern, and consumer code imports no MAF namespaces.

## Final Acceptance

The plan is complete when a new package can reference Tandem and implement a
pipeline from start to finish with this experience:

```csharp
internal sealed record SongPipelineParticipants(
    SongwriterAgent Songwriter,
    LintStage Lint,
    ProofreaderAgent Proofreader,
    CompleteSongStage Complete
);
```

```csharp
var pipeline = TandemWorkflow
    .Start(at: participants.Songwriter, name: "song")
    .Route(
        on: SongwriterAgent.Outcome.DraftWritten,
        from: participants.Songwriter,
        to: participants.Lint,
        label: "draft written"
    )
    .Route(
        on: LintStage.Outcome.Passed,
        from: participants.Lint,
        to: participants.Proofreader,
        label: "lint passed"
    )
    .Build(outputs: [participants.Complete]);
```

```csharp
services
    .AddTandem()
    .AddSongPipeline();
```

The consumer writes the song pipeline's invariants. Tandem supplies machinery.
MAF remains the internal graph and runtime substrate. No MAF type, binding,
execution context, workflow handle, or framework bootstrapping leaks into the
consumer package. Replacing MAF changes Tandem's adapters, not the song pipeline.
