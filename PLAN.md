# Tandem Pipeline Authoring

> Historical implementation plan. Runtime durability, restart, scheduler, and
> attach statements are superseded by `IN_PROCESS_RUNTIME_PLAN.md` and are not
> current product contracts.

## Goal

**Make Tandem an installable pipeline-authoring and execution library whose
consumer code expresses lifecycle invariants, steps, results, and routes while
Tandem supplies commodity execution machinery and Microsoft Agent Framework
remains the only workflow graph and orchestration engine.**

The flagship pipeline is **Delivery**. Delivery plans, implements, verifies, and
independently reviews a bounded software change. It is a first-party consumer of
the same public Tandem API available to external packages; it has no privileged
access to Tandem internals.

The desired consumer experience is:

```csharp
services
    .AddTandem()
    .AddDelivery();
```

Or, for a consumer-owned pipeline:

```csharp
services
    .AddTandem()
    .AddReleaseReview();
```

Pipeline authors should repeatedly write only their durable facts, invariants,
prompts, policies, state transitions, lifecycle actions, step-owned result unions,
steps, and routes. They should not repeatedly wire MAF executor
bindings, chat-client construction, process execution, session persistence,
lifecycle receipts, MCP transport, replay handling, observation, or durable
execution.

## Product Model

There are three distinct product concepts:

- **Tandem** is the installable authoring and execution library.
- **Delivery** is Tandem's batteries-included software-delivery pipeline.
- A **custom pipeline** is a consumer-owned state, step inventory, and
  workflow graph authored against Tandem's public API.

Delivery is not a base class and is never extended. A consumer that wants
different behavior creates and registers a different pipeline. Reusable Delivery
operations may be published later only when a concrete consumer proves that reuse
is valuable.

## Hard Boundaries

1. MAF remains the only executable graph, scheduler, orchestration engine,
   durability mechanism, suspension mechanism, agent loop, and tool dispatcher.
2. Tandem must not add route descriptors, a route registry, a graph AST, delayed
   graph compilation, a pipeline DSL, or another workflow runtime.
3. Routes declared through Tandem authoring helpers must immediately register
   real MAF edges.
4. A pipeline owns its concrete `TState`, step-owned Dunet result unions, prompts,
   policies, lifecycle-action contracts, and state transitions.
5. Tandem owns composition-neutral runtime bookkeeping and execution machinery.
6. There is no universal pipeline-state interface and no state bag.
7. There is no Delivery inheritance, pipeline inheritance, assembly scanning,
   magical discovery, or convention-based plugin loading.
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
Tandem.Delivery
Tandem.Tool
```

Do not create `Tandem.Abstractions`, `Tandem.Runtime`, or public `Domain`,
`Application`, and `Infrastructure` packages. Those names expose internal
architecture rather than consumer capabilities.

The step source generator may use a separate build project internally, but ships
as an analyzer/build asset of the `Tandem` package. It is not a fourth product
package that consumers select independently.

### Tandem

The public authoring API and reusable implementation:

- `PipelineMessage<TState>` and `PipelineRuntime`;
- step contracts, Dunet result adaptation, and agent usage bookkeeping;
- Tandem's step source generator and internal MAF step adapter;
- role-blind agent machinery and its configuration/policy contracts;
- an opaque Tandem `Pipeline` handle over the active execution substrate;
- lifecycle receipt persistence, replay, and conflict handling;
- composition-supplied lifecycle-action registration machinery;
- MAF workflow hosting and durable execution support;
- generic observation and projection machinery;
- commodity services such as command execution when reuse is proven.

### Tandem.Delivery

The first-party production pipeline:

- Delivery state, step-owned results, step IDs, and workflow graph;
- Delivery stages and agents;
- Delivery prompts, policies, and structured decisions;
- Delivery lifecycle actions and their registration;
- Delivery-specific Git, verification, human-decision, and run-state semantics.

`Tandem.Delivery` references only public Tandem APIs. Tandem does not reference
Delivery.

### Tandem.Tool

The executable/operator surface:

- CLI commands and configuration loading;
- composition selection and process startup;
- dashboard and terminal rendering;
- application-host wiring.

Physical extraction of `Tandem.Tool` may follow Delivery extraction if doing both
in one slice obscures the authoring boundary. `Program.cs` may remain hard-coded
to Delivery during this plan, but new shared runtime code must not deepen that
coupling.

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
| `Agent` | Model-backed step | `PlannerAgent`, `DebaterAgent` |
| `Stage` | Deterministic step | `VerificationStage`, `LintStage` |
| `Port` | Durable request/response boundary | `HumanInputPort` |
| `Action` | Validated model-invoked lifecycle action | `SubmitReportAction` |
| `Policies` | Named behavioral invariants | `ExecutorPolicies` |
| `Prompts` | Instructions and message projections | `ReviewerPrompts` |
| `Decision` | Structured model result | `PlannerDecision` |
| `Composition` | MAF graph declaration | `DeliveryComposition` |
| `Steps` | Typed executable-step inventory | `DeliverySteps` |
| `Result` | Dunet union returned by one step | `VerificationResult` |
| `State` | Durable pipeline-owned facts | `DeliveryState` |
| `Registration` | Explicit DI/package registration | `DeliveryRegistration` |

Avoid vague suffixes such as `Manager`, `Service`, `Processor`, `Handler`,
`Provider`, and `Helper` unless they identify a genuinely distinct role.

## Target Delivery Layout

```text
src/Tandem.Delivery/
|-- DeliveryComposition.cs
|-- DeliverySteps.cs
|-- DeliveryState.cs
|-- DeliveryStageIds.cs
|-- DeliveryRegistration.cs
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
    `-- DeliveryLifecycleActions.cs
```

This is a growth pattern, not mandatory empty scaffolding. Small agents may need
only `<Name>Agent.cs` and `<Name>Prompts.cs`. Tightly coupled contracts and
validators may share the action or decision file that owns them.

## Step Authoring Surface

Userland works with semantic steps and step-owned result unions, not MAF
`ExecutorBinding` values, pipeline-wide string outcomes, or framework-heavy
generic base declarations.

The desired inventory is a DI-constructible positional record:

```csharp
internal sealed record DeliverySteps(
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

### Dunet result contract

A step accepts its declared input and returns one case of its own Dunet union.
There is no independent `output + outcome` pair and no Tandem `Emit` helper; that
pair is a discriminated union in disguise. Declared results are values and
undeclared failures are exceptions.

The internal adapter repackages that transient typed result into the next
`PipelineMessage<TState>`. It preserves `PipelineRuntime`, takes the updated state
from the result case, and records a durable Tandem-owned result envelope containing
the stable step ID, case ID, and serialized case payload. MAF therefore continues
to route one message type between steps while generated `.Result.<Case>` selectors
match the durable step/case identity. Pipeline authors never construct or inspect
this envelope directly.

```csharp
using Dunet;
using Tandem;

[PipelineStage("verification")]
public sealed partial class VerificationStage(VerificationRunner verification)
{
    [Union]
    public partial record VerificationResult
    {
        public partial record Passed(DeliveryState State);
        public partial record Failed(DeliveryState State);
        public partial record InfrastructureFailed(
            DeliveryState State,
            string Reason
        );
    }

    public async ValueTask<VerificationResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await verification.RunAsync(
            pipeline.State.Workspace,
            pipeline.State.Commands,
            cancellationToken
        );

        var state = pipeline.State with { Verification = result };

        if (result.InfrastructureFailure is not null)
        {
            return new VerificationResult.InfrastructureFailed(
                state,
                result.InfrastructureFailure
            );
        }

        return result.Passed
            ? new VerificationResult.Passed(state)
            : new VerificationResult.Failed(state);
    }
}
```

Short Dunet cases stay on one line. Break only cases whose payload no longer fits
readably, as with `InfrastructureFailed` above.

Accept Dunet as a Tandem dependency. Do not hand-roll or partially reproduce its
union generation, matching, or exhaustiveness machinery. Tandem's source
generator may inspect the authored `[Union]` declaration, but it must not depend
on consuming Dunet-generated output because source generators cannot rely on
execution order or another generator's emitted syntax.

### Tandem step generator

Extension methods cannot add an implemented interface, instance state, stable
identity, a result-case facade, or a MAF adapter. An explicit Tandem marker and
source generator remove that ceremony from userland. The exact marker may differ
for an agent and a deterministic stage, but generated public signatures contain
no MAF types.

For the stage above, Tandem conceptually generates:

```csharp
partial class VerificationStage
    : IPipelineStep<VerificationStage.VerificationResult>
{
    public string Id => "verification";

    public ResultRoutes Result => new(this);

    public readonly struct ResultRoutes(VerificationStage step)
    {
        public ResultCase<VerificationStage, VerificationResult.Passed> Passed =>
            new(step);

        public ResultCase<VerificationStage, VerificationResult.Failed> Failed =>
            new(step);

        public ResultCase<
            VerificationStage,
            VerificationResult.InfrastructureFailed
        > InfrastructureFailed => new(step);
    }
}
```

`VerificationResult` is the authored Dunet type. `Result` is the generated,
instance-bound routing facade. Its `ResultCase` values bind one concrete step
instance to one case that the step can actually return. Their constructors are
hidden infrastructure API used by generated source; ordinary userland receives
them only through `.Result.<Case>`, so invalid step/result pairings are absent
from the authored composition grammar.

Tandem's generator owns only:

- the `IPipelineStep<TResult>` implementation and stable step identity;
- the instance-bound `.Result.<Case>` facade;
- the internal step-to-MAF executor adapter;
- mapping a returned Dunet case to the corresponding real MAF edge outcome.

Every authored result case carries the owning pipeline state as its first `State`
value. The generator uses that declared property to construct the successor
`PipelineMessage<TState>` without reflection, an untyped state carrier, or a
second graph representation. The full result case is serialized only as durable
routing/evidence payload; the concrete Dunet value remains strongly typed at the
step and generated-adapter boundaries.

Dunet owns the result union itself. Userland owns every result case and payload.
The authored `ExecuteAsync` signature supplies the input and result types, so the
step class needs no public generic base declaration. When workflow capabilities
are required, expose the smallest Tandem-owned capability contract, not MAF
`IWorkflowContext` and not a one-for-one mirror of it. Human suspension and
durable state access should use focused Tandem capabilities or steps.

The exact internal binding implementation requires a MAF lifecycle spike. The
safe default is one fresh binding per step per workflow build, reused for every
visit to that step within the built graph. Binding lifetime is not a userland
option. Cache bindings across workflow builds only if tests prove MAF defines
them as reusable and concurrent-safe. This may change Tandem internals but must
not change the public step syntax.

Binding lifetime is distinct from agent-session lifetime:

- Tandem owns build-scoped MAF binding creation;
- DI/hosting owns step-instance lifetime;
- userland policy owns conversation/session continuation, reset, promotion, and
  teardown decisions.

## Composition Syntax

`<Name>Composition.cs` is the complete route map. It receives the typed step
inventory and contains no step construction or infrastructure wiring. Composition
uses one fluent Tandem builder whose route calls immediately register real MAF
edges:

```csharp
public sealed class DeliveryComposition(DeliverySteps delivery)
{
    public Pipeline Build()
    {
        return TandemWorkflow
            .Start(
                at: delivery.PrepareWorkspace,
                name: "delivery",
                description:
                    "Plan, implement, verify, and independently review a software change."
            )
            .Route(
                on: delivery.PrepareWorkspace.Result.Prepared,
                to: delivery.Executor,
                label: "workspace prepared"
            )
            .Route(
                when: IsUnexpectedPrepareResult,
                from: delivery.PrepareWorkspace,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.CaptureCandidate.Result.Captured,
                when: pipeline => pipeline.State.Packet.Verification.Count > 0,
                to: delivery.Verification,
                label: "verification configured"
            )
            .Route(
                on: delivery.Verification.Result.Passed,
                to: delivery.Reviewer,
                label: "verification passed"
            )
            .Route(
                on: delivery.Verification.Result.Failed,
                to: delivery.Executor,
                label: "verification failed"
            )
            .Route(
                from: delivery.HumanDecision.Request,
                to: delivery.HumanDecision.Port,
                label: "request human input"
            )
            .Route(
                from: delivery.HumanDecision.Port,
                to: delivery.HumanDecision.ApplyResponse,
                label: "answer received"
            )
            .Build(
                outputs: [delivery.CompleteRun, delivery.FailRun]
            );
    }
}
```

The graph uses one `Route` operation with strongly typed overloads:

```csharp
Route(from:, to:, label:);
Route(on:, to:, label:);
Route(when:, from:, to:, label:);
Route(on:, when:, to:, label:);
```

`on:` accepts a generated, instance-bound result case such as
`delivery.Verification.Result.Passed`. That value already contains the source
step, so a result-based route has no independent `from:` argument to mismatch.
`when:` supplies a Boolean pipeline predicate. Predicate-only and unconditional
routes still name `from:` explicitly.

All non-obvious arguments use named parameters. Conditional route calls lead with
`on:` or `when:` so the reason for the route is visible first. Unconditional
routes lead with `from:`. Predicate parameters use `pipeline` and receive
`PipelineMessage<DeliveryState>`:

```csharp
pipeline.State
pipeline.Runtime
pipeline.LatestResult
```

The generated selector makes an invalid step/result pair unrepresentable:
`delivery.Verification.Result.Approved` simply does not exist. Compatibility is
enforced at compile time, not deferred to graph construction.

MAF `Workflow`, `WorkflowBuilder`, `IWorkflowContext`, `Executor`,
`ExecutorBinding`, and request-port types do not appear in public authoring
signatures or route-authoring code. Tandem's opaque `Pipeline` contains the built
runtime workflow but no duplicate graph representation.

### Route-helper boundary

The fluent Tandem builder is a zero-storage adapter proven by Delivery and the
consumer sample. Each `Route` overload must:

1. Accept semantic `IPipelineStep` and generated `ResultCase` values.
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
IsUnexpectedPlannerResult
IsUnexpectedVerificationResult
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

`<Name>Agent.cs` assembles role-blind Tandem agent machinery from framework
services supplied by DI and invariant-bearing functions supplied by the pipeline.
Like a deterministic stage, each agent declares one Dunet result union and
returns one case from each invocation.

Call sites use behavioral names:

```csharp
mutationPolicy: ExecutorPolicies.RequirePlannerApprovalForWrites,
continuationPolicy: ExecutorPolicies.RequireExecutorLifecycleResult,
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
application. Delivery or another consumer pipeline supplies the decisions.

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

Tandem must not infer lifecycle behavior from step IDs, tool names, or Delivery
roles. Do not add checks such as `step.Id == "reviewer"` or generic
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

Each deterministic graph step is named `<Name>Stage` and normally lives
in `Stages/<Name>Stage.cs`.

A stage:

1. Receives a typed pipeline message or another explicit declared input type.
2. Performs one operation.
3. Returns one case of its step-owned Dunet result union.
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
persistence, while keeping state interpretation and lifecycle results owned by
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

Delivery explicitly registers its lifecycle-action set:

```csharp
internal static class DeliveryLifecycleActions
{
    public const string Name = "delivery";

    public static IMcpServerBuilder Register(IServiceCollection services) =>
        services
            .AddMcpServer()
            .WithTools<AskPlannerAction>()
            .WithTools<SubmitReportAction>()
            .WithTools<WriteCheckpointAction>();
}
```

Remove the generic agent configuration's default MCP identity. A step with
lifecycle actions must select its action registration explicitly.

The current shared-host `RunSimpleV1Async`/`RunDebateAsync` switch is not the final
extension surface. Replace it with the minimum explicit registration mechanism
needed for Delivery and the consumer proof. Do not use assembly discovery. `Program.cs`
selection may remain hard-coded during this plan.

## DI Pattern

`AddTandem` registers stable commodity machinery once. Expected responsibilities
include chat-client creation, lifecycle receipt storage, MCP client creation,
agent session persistence, workspace/process machinery, observation, and MAF
hosting where those services are composition-neutral.

`AddDelivery` registers Delivery steps, the typed step inventory, its composition,
and its lifecycle-action set:

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
services.AddTransient<DeliverySteps>();
services.AddTransient<DeliveryComposition>();
```

The exact registrations belong only in `DeliveryRegistration.cs`; they do not appear
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
|-- ReleaseReviewSteps.cs
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
3. Implement deterministic `<Name>Stage` steps with one nested Dunet result union
   each.
4. Assemble model-backed `<Name>Agent` steps with prompts, policies, and one
   nested Dunet result union each.
5. Let Tandem generate each step's identity, MAF adapter, and `.Result.<Case>`
   routing facade from its authored contract.
6. Implement composition-owned lifecycle actions.
7. Declare the positional `<Name>Steps` inventory.
8. Declare the complete route map in `<Name>Composition`.
9. Register services, composition identity, and lifecycle actions in
   `<Name>Registration`.
10. Prove graph shape, state serialization, execution, replay, conflict handling,
    and durability.

Add `docs/pipeline-authoring.md` containing this recipe, a minimal example, and
links to Delivery as the complete production example.

## External Consumer Proof

Graduate Debate from an internal test fixture into a separate consumer-style
sample/test project:

```text
samples/Tandem.Sample.Debate/
|-- Tandem.Sample.Debate.csproj
|-- DebateComposition.cs
|-- DebateSteps.cs
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
- Delivery state, results, stage IDs, policies, prompts, or lifecycle actions;
- edits to Tandem runtime to add Debate-specific switches.

The proof must exercise:

- `PipelineMessage<DebateState>`;
- at least two deterministic/model-backed step categories;
- structured JSON state transition;
- a revision loop;
- a Debate-owned lifecycle action;
- receipt replay without a repeated model call;
- conflict detection;
- runtime session, usage, and invocation bookkeeping;
- graph reflection containing only Debate steps and routes;
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
- start step;
- step IDs;
- request ports and their input/output types;
- routes and whether they are conditional;
- terminal output steps;
- Mermaid output;
- Graphviz DOT output.

Do not parse Mermaid or DOT back into a Tandem model. Reflection supplies
structured inspection; Mermaid and DOT are export formats only. Treat MAF's full
rendered formatting as framework-owned and potentially unstable. Tandem tests
should pin semantic IDs, topology, labels, and valid format markers rather than
snapshot every character of generated output.

The eventual operator surface should support commands equivalent to:

```text
tandem graph delivery
tandem graph delivery --format mermaid
tandem graph delivery --format dot
tandem graph delivery --output delivery.mmd
tandem describe delivery
```

Exact CLI syntax may be selected when `Tandem.Tool` composition selection is
implemented. The library-level inspection/export capability must not depend on
the CLI and should work for external pipeline packages.

Acceptance tests must prove:

1. Inspection uses the built MAF `Workflow`, not route declarations retained by
   Tandem.
2. Every reflected route endpoint resolves to a reflected step or port.
3. Route labels appear in Mermaid and DOT output where MAF supports them.
4. Mermaid output starts with a valid flowchart declaration and includes the
   start step.
5. DOT output starts with a valid directed-graph declaration.
6. Delivery and the external Debate sample can both be inspected without
   composition-specific inspection code.
7. Exported diagrams change when and only when the executable MAF graph changes.

## MAF Replacement Boundary

MAF is deliberately replaceable infrastructure, not part of Tandem's public
authoring model. This boundary does not make runtime replacement free; it confines
replacement cost to Tandem.

Portable Tandem and pipeline-owned concepts include:

- typed state, runtime facts, Dunet results, and state transitions;
- steps and their declared input/result contracts;
- prompts, decisions, validators, and policies;
- lifecycle-action contracts and receipt semantics;
- route intent expressed through Tandem authoring operations;
- inspection data exposed from the opaque Tandem `Pipeline`.

Substrate-owned adapters include:

- step-to-executor adaptation and binding;
- graph construction and workflow hosting;
- suspension and external resumption;
- durable orchestration history;
- workflow event projection;
- runtime graph reflection and visualization;
- agent-loop and tool-dispatch integration where MAF owns those mechanics.

A future MAF replacement rewrites those adapters and starts new runs on the new
substrate. It must not require rewriting Delivery or external pipeline state,
stages, agents, policies, lifecycle actions, or route declarations. Historical
MAF orchestration instances are not a Tandem portability contract.

## Mechanical Enforcement

Add tests or project-level architecture checks proving:

1. Tandem does not reference `Tandem.Delivery`.
2. `Tandem.Delivery` references only public Tandem APIs.
3. The consumer sample references Tandem and not Delivery.
4. Delivery and consumer projects have no direct MAF package reference or MAF
   namespace import.
5. Generic Tandem code contains no Delivery step IDs, results, prompts,
   lifecycle-action names, or state assumptions.
6. No generic MCP configuration defaults to Delivery.
7. Pipeline state contains no services, framework contexts, or mutable state bag.
8. Every composition has graph-reflection and closed-generic serialization tests.
9. Every lifecycle action has validation, replay, and conflict tests.
10. Route helpers register MAF edges immediately and retain no parallel route
   representation.
11. Delivery preserves its characterized topology and behavioral tests through
    the refactor unless this plan explicitly changes an identity.
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
17. Every step returns one case of an authored Dunet result union; no separate
    output/outcome pair or Tandem union implementation exists.
18. Generated `.Result.<Case>` selectors bind the concrete step instance and
    result case so invalid step/result combinations are unrepresentable.
19. Every fluent `Route` call immediately registers a real MAF edge, returns the
    same builder, and retains no staged or parallel route representation.
20. Tandem-generated source contains no pipeline-specific result semantics and
    does not depend on consuming Dunet-generated output.

## Execution Plan

### Slice 0: Characterize MAF and establish the correctness ledger

Before changing the public authoring surface:

1. Keep the existing test suite green as the baseline evolves.
2. Add focused tests for binding the same executor into one and multiple workflow
   builds.
3. Determine whether `ExecutorBinding` may be cached, must be fresh per workflow,
   or carries workflow-specific identity.
4. Test concurrent workflow builds and repeated visits to one step within a loop.
5. Default to fresh per-build bindings unless reuse is proven safe.
6. Prove a plain Tandem step with no public MAF inheritance can be wrapped by an
   internal MAF executor while preserving type validation, graph reflection,
   observation decoration, request ports, and durable identity.
7. Record the result in tests and use it to select the internal step-adapter
   implementation.
8. Record every confirmed pre-refactor correctness finding with its intended
   invariant, reproduction path, target regression test, and owning slice while
   keeping the baseline green.
9. Assign the findings to the earliest slice that owns the affected seam:
   - Delivery conversion owns planner/reviewer read-only access, executor mutation
     authority, role-specific checkpoint policy, reviewer human-answer
     restoration, and the durable human-resume proof;
   - runtime registration owns lifecycle-action receipt replay and MCP process
     cleanup;
   - DI and host wiring owns attach metadata timing;
   - the external consumer proof owns removal of privileged Debate coupling.
10. At the start of each owning slice, add the regression test, observe it fail,
    fix it before structural movement, and keep it as a permanent proof.
11. Do not preserve a confirmed bug merely because the existing graph exhibits
    it, and do not combine an uncharacterized behavior fix with package movement.

Exit gate: MAF binding and adapter behavior is proven, every known correctness
finding has an intended invariant, reproduction, target test, and owning slice,
and the desired plain-step syntax rests on evidence rather than assumptions.

### Slice 1: Prove the complete authoring vertical

1. Accept Dunet as a Tandem dependency; do not reimplement discriminated unions.
2. Add the smallest public `IPipelineStep<TResult>` contract and explicit
   userland marker for generated adaptation.
3. Ensure no public step type inherits MAF `Executor` or accepts
   `IWorkflowContext`.
4. Generate stable identity, the internal MAF adapter, and the instance-bound
   `.Result.<Case>` facade from authored step and Dunet declarations.
5. Add the opaque Tandem `Pipeline`, `TandemWorkflow.Start`, and the minimum
   immediate MAF route adapter needed to accept semantic steps and result cases.
6. Keep MAF workflows, builders, request ports, and bindings internal to Tandem
   authoring machinery.
7. Prove Tandem's generator reads authored Dunet declarations without consuming
   Dunet-generated output or relying on generator order.
8. Prove one representative plain partial stage end to end through:
   - Dunet result construction and serialization;
   - generated `.Result.<Case>` routing;
   - heterogeneous successor steps and request ports;
   - observation decoration and semantic graph reflection;
   - real in-process MAF execution;
   - real durable closed-generic execution across process restart.

Exit gate: `sample.Verification.Result.Passed` executes, reflects, serializes, and
resumes durably without authored MAF types, a generic framework base declaration,
`ExecutorBinding`, `BindExecutor`, or an `Emit` helper.

### Slice 2: Rename SimpleV1 to Delivery

1. Rename production SimpleV1 types, namespaces, workflow metadata, tests, and
   documentation to Delivery.
2. Rename construction-oriented policy methods to behavioral names.
3. Remove dead SimpleV1 concepts and the obsolete waiting stage if graph
   characterization confirms they are unused.
4. Preserve the characterized 26-edge lifecycle shape while distinguishing
   confirmed bugs from intended behavior through the Slice 0 ledger.
5. Because the project is greenfield, do not retain SimpleV1 aliases or old
   workflow identities.

Exit gate: no production or test code refers to SimpleV1, and the rename changes
identity rather than behavior.

### Slice 3: Convert Delivery to the public authoring model

Keep Delivery colocated while changing its shape so behavior fixes and authoring
changes are not obscured by project movement:

1. Add `DeliverySteps` as the positional DI-constructed inventory.
2. Delete `CreateNodes` and all userland binding ceremony.
3. Reduce `DeliveryComposition` to workflow metadata, fluent route declarations,
   terminal graph outputs, and short graph predicates.
4. Use the strongly typed fluent `Route` overloads with named `from`, `on`,
   `when`, `to`, and `label` parameters.
5. Split agent assembly, prompts, policies, and decisions by the agreed grammar.
6. Split deterministic steps into `<Name>Stage.cs` files.
7. Split lifecycle tools into `<Verb><Noun>Action.cs` files.
8. Move Delivery routing results into Dunet unions nested with their owning steps;
   delete the pipeline-wide outcome catalog.
9. Generate `.Result.<Case>` for every authored result union and remove separate
   `from:` arguments from result-based routes.
10. Adapt deterministic stages and role-blind agent machinery through the proven
    Tandem step adapter.
11. Enforce planner/reviewer read-only access and executor mutation authority in
    named Delivery policies.
12. Attach checkpoint behavior only to the roles that own it.
13. Preserve and apply reviewer human answers correctly, and strengthen the
    durable human-suspension proof through actual resume assertions.

Exit gate: `DeliverySteps.cs` shows what exists, `DeliveryComposition.cs` shows
exactly how it flows, Delivery contains no binding ceremony, and its assigned
correctness tests pass.

### Slice 4: Generalize runtime and lifecycle-action registration

Remove the pipeline-specific runtime dependencies that would otherwise force
Tandem to know Delivery after extraction:

1. Replace shared `RunSimpleV1Async`/`RunDebateAsync` methods with the minimum
   explicit registration-by-identity seam.
2. Keep registration deterministic and explicit; do not scan assemblies.
3. Ensure each agent with lifecycle tools selects its composition action set.
4. Remove the default SimpleV1/Delivery MCP identity from generic agent config.
5. Make workflow hosting, observation, and projection composition-neutral.
6. Fix lifecycle-action receipt replay so accepted transitions survive replay
   without repeating the model call.
7. Preserve conflict detection, validation, and mechanical turn termination.
8. Dispose MCP clients, hosts, and child processes on every completion, failure,
   cancellation, and restart path.
9. Leave final CLI composition selection hard-coded if necessary; the application
   host may know Delivery, Tandem runtime may not.

Exit gate: Tandem hosts any explicitly registered action set without a
consumer-specific switch, receipt replay is idempotent, and MCP processes do not
leak.

### Slice 5: Establish DI and policy boundaries

1. Add `AddTandem` registrations for composition-neutral machinery.
2. Add `AddDelivery` registrations for Delivery steps, inventory, composition,
   and lifecycle-action set.
3. Constructor-inject stable dependencies into stages and agents.
4. Pass dynamic build observation through an explicit build context.
5. Remove service resolution and repeated machinery construction from Delivery.
6. Add Tandem-owned session, profile-selection, and teardown policy contracts and
   decision values.
7. Move all executor/reviewer/critic retention, reset, promotion, and teardown
   behavior into named Delivery policies.
8. Persist profile decisions before model calls and apply session lifecycle
   transitions atomically and replay-safely.
9. Persist run metadata before exposing an attachable run ID, and prove immediate
   attach observes the correct composition and packet metadata.

Exit gate: Delivery code repeatedly expresses invariants rather than framework
plumbing, and Tandem plus Delivery can be constructed using only their public DI
registrations.

### Slice 6: Extract Tandem.Delivery

Only move projects after the authoring, runtime-registration, and DI seams are
green:

1. Create `Tandem.Delivery.csproj` referencing Tandem.
2. Move Delivery state, step-owned results, IDs, stages, agents, prompts,
   policies, decisions, lifecycle actions, composition, and registration into it.
3. Promote only the Tandem APIs required by Delivery to public authoring
   contracts.
4. Do not use `InternalsVisibleTo`.
5. Add project-reference checks enforcing `Tandem ->/ Delivery` and
   `Delivery -> Tandem`.
6. Give `Tandem.Delivery` no direct MAF package reference or namespace import.
7. Keep application-host selection in `Tandem.Tool` or the current composition
   root rather than reintroducing Delivery knowledge into Tandem.

Exit gate: Delivery builds, executes, and passes durable proofs as an unprivileged
package consumer, and the extraction itself contains no behavior changes.

### Slice 7: External Debate consumer proof

1. Create the separate Debate sample/test project referencing only Tandem.
2. Apply the same state, step, agent, stage, Dunet-result, lifecycle-action,
   composition, and registration grammar.
3. Port the existing deterministic Debate behavior and strengthen it with
   registration and conflict proofs.
4. Run in-process and durable closed-generic workflows.
5. Give Debate at least two distinct userland lifecycle policies, such as a
   retained critic session and a fresh or torn-down judge session, and prove the
   decisions survive loops and receipt replay.
6. Remove source inclusion, `InternalsVisibleTo`, Tandem runtime switches, and any
   other privileged coupling used by the existing fixture.

Exit gate: Debate requires no Delivery vocabulary, privileged access, or Tandem
source change, closing its Slice 0 correctness-ledger item.

### Slice 8: Inspection, documentation, and enforcement

1. Add `docs/pipeline-authoring.md` with the complete author journey.
2. Update README positioning: Tandem is the engine; Delivery is the flagship
   pipeline.
3. Update CONTRIBUTING boundaries and naming grammar.
4. Add library-level workflow inspection with Mermaid and DOT export over the
   built MAF workflow.
5. Add Delivery and external Debate graph-inspection tests, preserving semantic
   graph assertions without brittle full-render snapshots.
6. Document the future `tandem graph` and `tandem describe` operator surface
   without coupling the library implementation to current CLI wiring.
7. Add architecture checks, source-generator tests, and project-reference tests.
8. Verify that every Slice 0 correctness-ledger item is closed by a durable or
   public-boundary proof.
9. Run formatting, analyzers, all tests, build, durable proofs, diff checks, and
   the repository architecture checker.

Exit gate: documentation, project structure, public API, Delivery, and Debate all
teach the same authoring pattern; consumer code imports no MAF namespaces; and no
known correctness-ledger item remains open.

## Final Acceptance

The plan is complete when a new package can reference Tandem and implement a
pipeline from start to finish with this experience:

```csharp
[PipelineStage("lint")]
internal sealed partial class LintStage
{
    [Union]
    public partial record LintResult
    {
        public partial record Passed(SongState State);
        public partial record Failed(SongState State, string Reason);
    }

    public ValueTask<LintResult> ExecuteAsync(
        PipelineMessage<SongState> pipeline,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<LintResult>(
        new LintResult.Passed(pipeline.State)
    );
}
```

```csharp
internal sealed record SongSteps(
    SongwriterAgent Songwriter,
    LintStage Lint,
    ProofreaderAgent Proofreader,
    CompleteSongStage Complete
);
```

```csharp
var pipeline = TandemWorkflow
    .Start(at: song.Songwriter, name: "song")
    .Route(
        on: song.Songwriter.Result.DraftWritten,
        to: song.Lint,
        label: "draft written"
    )
    .Route(
        on: song.Lint.Result.Passed,
        to: song.Proofreader,
        label: "lint passed"
    )
    .Build(outputs: [song.Complete]);
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
