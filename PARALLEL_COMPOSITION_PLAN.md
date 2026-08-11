# Parallel Composition

## Status

Implemented on 2026-08-11 for C# Core, the TypeScript SDK, and registration contract v6.

The static parallel-all capability, deterministic merge, state/runtime isolation, semantic
inspection, persistence policy, observation serialization, bridge validation, type tests, and
end-to-end runtime proof are in place. Arbitrary branch subgraphs, nested parallel groups, dynamic
fan-out, races, quorums, and partial-success merging remain intentionally deferred as described
below. Release examples and broader documentation remain separate follow-up work.

This plan adds first-class static parallel composition to Tandem's C# and TypeScript authoring APIs.
It is a general library capability motivated by independent work such as concurrent classification,
retrieval, review, and enrichment. It is not a Waduno-specific helper and does not expose Microsoft
Agent Framework mechanics through Tandem's public API.

## Decision

Add one parallel-all composition primitive with these semantics:

1. A parallel group owns two or more named Tandem branch participants.
2. Every branch receives an isolated copy of the same pre-fork state.
3. Branches execute concurrently.
4. Every branch must succeed before merge runs.
5. Merge receives the original baseline and successful branch states in declaration order.
6. Merge is mandatory, synchronous, explicit, and deterministic.
7. The merged state continues through ordinary outcome routing.
8. Branch agents and stages remain real Tandem participants with their own IDs, observations,
   structured-output behavior, usage, and optional persistence.

The first version supports one existing Tandem participant per branch. It does not support arbitrary
branch subgraphs.

Conceptually:

```text
                       -> world agent ---------
discovery -> fork      -> epistemic agent -----+-> merge -> assemble framing
                       -> temporal agent ------
```

## Why This Shape

### Not an Opaque Parallel Stage

This would be easy to implement:

```ts
stage({
  id: "classify",
  execute: async state => Promise.all([
    callWorldModel(state),
    callEpistemicModel(state),
    callTemporalModel(state),
  ]),
});
```

It is not the desired Tandem feature. The three operations would be hidden inside one stage and would
not receive Tandem agent behavior independently. Their structured-output acceptance, model usage,
fault identity, accepted values, and graph participation would have to be rebuilt manually.

### Not Arbitrary Parallel Subgraphs

Allowing each branch to contain its own routes, cycles, terminals, interactions, nested parallel
groups, and partial outcomes immediately creates a second graph-composition language inside one
node. That can be added later if multiple real pipelines prove the need.

One participant per branch supports the motivating use cases while preserving a small static graph
contract.

### No Implicit State Merge

Every current Tandem node transforms the shared `TState`. Running several nodes from one baseline
produces several valid states. Selecting the last completed branch would make application behavior
depend on scheduling. Object spread, reflection, serialization, and property-level automatic merging
would all hide ownership and conflict policy.

The application must state how branch results combine.

## Confirmed Microsoft Agent Framework Behavior

Tandem currently uses Microsoft Agent Framework 1.16.0 and executes through
`InProcessExecution.Concurrent`.

The pinned runtime already provides:

- `WorkflowBuilder.AddFanOutEdge(...)`;
- `WorkflowBuilder.AddFanInBarrierEdge(...)`;
- concurrent execution of distinct target executors in a superstep.

Important verified behavior:

1. Fan-out broadcasts the same message and payload reference to every branch. It does not clone.
2. Different branch executors run concurrently.
3. Fan-in waits until every configured source has emitted at least one message.
4. Fan-in then streams buffered messages to its sink as separate handler invocations in one delivery
   batch. It does not create a typed collection.
5. Fan-in delivery order follows runtime arrival grouping, not branch declaration order.
6. The barrier resets and can be visited again through a pipeline loop.
7. A branch exception faults the workflow after the current concurrent superstep. Siblings already
   running may complete; MAF does not provide immediate fail-fast sibling cancellation.
8. Caller cancellation reaches every active executor through the shared run cancellation token.
9. Fan-in state is isolated per pipeline run, including concurrent runs of one workflow.

These mechanics are sufficient, but Tandem must add branch cloning, collection, ordering, runtime
merge, and semantic inspection itself.

## Public Semantics

### Static Parallel-All

The first primitive means `all`, not `allSettled`, `race`, quorum, map, or dynamic fan-out.

- Branch count and identity are fixed when the pipeline is built.
- Every branch starts once per parallel-group occurrence.
- Merge runs exactly once when every branch succeeds.
- Branch declaration order defines merge order.
- Branch completion and observation order remain nondeterministic.
- A declared branch failure produces a declared parallel-group failure.
- A branch exception faults the whole run.
- Caller cancellation cancels the whole run.

Later concurrency forms must receive separate names and contracts rather than flags on `parallel`.

### Supported Branch Participants

Version one permits:

- generated state/pass-through stages;
- standard-outcome agents or generated outcome stages.

Version one rejects:

- terminals;
- interactions;
- another parallel group;
- branch-local subgraphs;
- one participant reused in multiple branches or elsewhere in the parent graph.

Parallel human interactions require separate terminal-broker and UI design because the current CLI
supports only one pending human prompt.

### Branch Success and Failure

For an ordinary stage, completion is branch success.

For a standard-outcome participant:

- `Success` is branch success;
- `Failed` is a declared branch failure;
- failure is not an exception and does not cancel successful siblings;
- merge does not run if any branch declares failure;
- after every branch settles, the parallel group returns the first declared failure in branch
  declaration order.

Selecting failure by declaration order makes the result stable across different completion orders.
Later support for application-defined partial success should be a distinct combinator.

### Exceptions

- Clone failure faults the parallel group before branch execution.
- Branch exception faults the workflow.
- Siblings already executing may finish before MAF reports the superstep failure.
- No merge or downstream route runs after a branch exception.
- Branch side effects are not rolled back.
- Merge exception faults the group and the run.

The API must not promise fail-fast sibling cancellation that MAF does not provide.

### Cancellation

- Caller cancellation propagates to every active branch.
- Merge does not run after cancellation.
- Cooperative branch operations should observe the shared cancellation token.
- Cancellation observations remain timing-dependent at individual branches, but the run result is
  cancelled consistently.

## C# Authoring API

The exact names can be tightened during the vertical slice, but the semantic shape is fixed.

```csharp
var classifications = PipelineNodes.Parallel(
    id: "classify-framing",
    clone: static state => state with { },
    branches:
    [
        PipelineBranch.Create("world", worldAgent),
        PipelineBranch.Create("epistemic", epistemicAgent),
        PipelineBranch.Create("temporal", temporalAgent),
    ],
    merge: static results =>
        results.Baseline with
        {
            World = results.State("world").World,
            Epistemic = results.State("epistemic").Epistemic,
            Temporal = results.State("temporal").Temporal,
        }
);

var pipeline = Pipeline
    .Start(discovery, "atomize")
    .Route(discovery.Success, classifications, "classify framing")
    .Route(classifications.Success, assembleFraming, "framing classified")
    .Route(classifications.Failed, failed, "classification failed")
    .Build(completed, failed);
```

Proposed concepts:

```csharp
public sealed class PipelineParallel<TState> : IStandardOutcomePipelineStep<TState>;

public sealed record PipelineBranch<TState>(
    string Id,
    IPipelineNode<TState> Participant
);

public sealed class PipelineParallelMerge<TState>
{
    public TState Baseline { get; }
    public IReadOnlyList<string> BranchIds { get; }
    public TState State(string branchId);
}
```

Use branch IDs rather than participant IDs as merge keys. The branch name expresses its role inside
the group and allows future reuse of participant implementations without coupling merge code to
physical executor names.

The merge delegate returns `TState`, not `Outcome<TState>` in version one. Branch failure policy is
the group contract; application-level outcomes belong in explicit downstream stages.

### Clone Contract

C# requires an explicit `Func<TState, TState>` clone function.

Tandem cannot clone through JSON because current public tests deliberately support non-serializable
and identity-sensitive state. A shallow record copy is insufficient when nested collections are
mutable.

The clone function:

- receives the baseline state;
- is called once per branch in declaration order before fan-out;
- must not mutate its input;
- must isolate every mutable application object reachable by branch code;
- may return the same value only when the reachable state graph is immutable.

Tandem documents and tests this contract but cannot prove deep immutability.

## TypeScript Authoring API

TypeScript uses the same semantic participant rather than a bridge-only `Promise.all` stage:

```ts
const classifications = parallel({
  id: "classify-framing",
  branches: {
    world: worldAgent,
    epistemic: epistemicAgent,
    temporal: temporalAgent,
  },
  merge: (baseline, results) => ({
    ...baseline,
    world: results.world.world,
    epistemic: results.epistemic.epistemic,
    temporal: results.temporal.temporal,
  }),
});

const atomization = pipeline({
  name: "atomize",
  state: AtomizationState,
  nodes: [discovery, classifications, assembleFraming, completed, failed],
  start: discovery,
  routes: [
    route({ from: discovery, outcome: "success", to: classifications, label: "classify" }),
    route({
      from: classifications,
      outcome: "success",
      to: assembleFraming,
      label: "classified",
    }),
    route({ from: classifications, outcome: "failed", to: failed, label: "failed" }),
  ],
  outputs: [completed, failed],
});
```

Proposed type shape:

```ts
export interface Parallel<TState> extends Participant<TState> {
  readonly kind: "parallel";
}

export function parallel<
  TState,
  const TBranches extends Readonly<
    Record<string, Stage<TState> | Agent<TState>>
  >,
>(definition: {
  readonly id: string;
  readonly branches: TBranches;
  readonly merge: (
    baseline: TState,
    results: { readonly [K in keyof TBranches]: TState },
  ) => TState;
  readonly persist?: boolean;
}): Parallel<TState>;
```

If TypeScript inference cannot infer `TState` cleanly from the branch object, use the established
curried form `parallel<TState>()({...})`. Do not require users to duplicate a result-map type.

TypeScript does not expose a clone callback. Every branch callback already receives state by parsing
the immutable JSON boundary independently. The bridge supplies a Core clone function that creates an
independent `JavaScriptState` envelope over the same immutable JSON text.

### TypeScript Membership

The parallel group is the parent pipeline participant. Its branches are owned nested participants:

- list the parallel group in `pipeline.nodes`;
- do not list branch participants separately;
- route only to and from the group;
- branch participants remain visible in inspection and observations;
- a branch participant cannot also appear in the parent node list or another group.

This prevents orphan validation from treating branch nodes as ordinary route participants and makes
ownership explicit.

## State and Runtime Isolation

### Application State

The fork must not send the same mutable `TState` reference directly to branch participants.

The physical fork executor invokes the clone function sequentially before scheduling branches. It
produces one prepared branch message per declaration. Branch clone code therefore cannot race while
reading the baseline.

### Pipeline Runtime

`PipelineRuntime` contains agent sessions, usage, invocation counts, profile selections, and gate
latches. Each branch requires an independent runtime clone as well as an application-state clone.

At join, Tandem performs a deterministic three-way runtime merge:

1. Retain the pre-fork runtime baseline.
2. Compare every successful branch runtime with that baseline.
3. Apply branch deltas in declaration order.
4. Accept equal changes to the same key.
5. Reject conflicting changes to the same key with a deterministic merge error.
6. Merge gate-latch additions and removals relative to the baseline; reject contradictory changes.
7. Preserve the run ID and shared run context.

Globally unique participant IDs should make ordinary session, usage, invocation, and profile keys
disjoint. Conflict detection remains mandatory because silent last-writer-wins would be unsafe.

### Occurrence Identity

A pipeline may loop through the same parallel group. Every visit needs a unique occurrence ID:

```text
{runId}--{parallelId}--{invocationNumber}
```

Branch-exit and join messages carry this identity. The collector rejects missing, duplicate, stale,
or mixed-occurrence branch results. Completed occurrence state is discarded after join.

## Physical Core Graph

One semantic parallel group expands internally to:

```text
semantic input route
    -> fork executor
        -> fan-out
            -> branch adapter 0 -> authored participant 0 -> exit 0
            -> branch adapter 1 -> authored participant 1 -> exit 1
            -> branch adapter N -> authored participant N -> exit N
        -> fan-in barrier
            -> collector/join executor
                -> semantic output route
```

### Fork Executor

- creates the occurrence ID;
- stores the baseline state and runtime;
- clones application state and runtime once per branch;
- tags each prepared message with group, occurrence, branch ID, and declaration index;
- emits prepared branch messages for fan-out selection.

MAF fan-out itself broadcasts one object. The prepared payload must therefore contain independently
created branch messages, and each branch adapter selects only its assigned message.

### Branch Exit

- receives exactly one authored participant result;
- captures branch success or declared failure;
- retains the branch-local state, runtime, and outcome;
- emits one correlated result to the barrier.

### Fan-In and Collector

MAF's barrier provides synchronization but streams messages individually. The collector must use the
delivery-batch lifecycle to gather the released messages and emit one joined input after the batch
finishes.

It must:

- group by occurrence ID;
- reject duplicate branch IDs;
- require exactly the declared branch set;
- sort by declaration index rather than arrival order;
- avoid retaining completed occurrences;
- support repeated group visits and concurrent pipeline runs.

### Join Executor

- skips application merge if any branch declared failure;
- chooses the first declared failure by branch declaration order;
- otherwise merges internal runtime;
- calls the application merge exactly once;
- clears internal parallel metadata;
- emits one normal `PipelineMessage<TState>` carrying the group outcome.

Internal fork, adapters, exits, and collector are physical implementation details. They do not appear
as authored participants or accepted values.

## Routing and Graph Validation

The parallel group behaves like a standard-outcome participant:

- incoming routes target the group;
- outgoing routes select `Success` or `Failed`;
- conditional outcome routes remain supported;
- the group may be the pipeline start;
- the group cannot be a terminal output;
- the group can be revisited through an outer graph loop.

Add validation for:

- at least two branches;
- nonblank, unique group and branch IDs;
- globally unique authored participant IDs, including nested branch participants;
- no terminal, interaction, or parallel branch participant;
- no branch participant shared with the parent graph, another group, or another branch;
- no internal physical-ID collision with authored IDs;
- one result emitted per branch occurrence;
- merge and clone delegates present;
- every branch uses the parent's exact `TState`;
- persistence references only owned participants;
- start/output and reachability checks understand nested branch ownership.

Do not infer semantic ownership from generated physical-ID suffixes. Record explicit physical-to-
semantic mappings during builder expansion.

## Observations

Ordinary branch participants continue to emit their existing observations under their actual IDs:

- `stepStarted`;
- text and reasoning updates for agents;
- usage;
- structured-output acceptance;
- `stepCompleted`, `stepFaulted`, or `stepCancelled`.

The join emits the parallel group's normal completion under the group ID. A persisted successful join
accepts the merged state under that ID.

Version one does not add public fork/branch/join observation variants. Branch membership is static
graph metadata, and branch step IDs already identify live work. Add dedicated lifecycle observations
only if a real consumer cannot reconstruct useful behavior from inspection plus existing events.

### Observation Concurrency

Parallel branches can publish observations concurrently. Existing observers are not documented as
concurrency-safe, and simple consumers commonly append to unsynchronized collections.

Preserve the current effective serial-delivery contract by serializing observer calls per run inside
`PipelineRunContext`. A slow observer continues to apply backpressure. Serialization must not imply
deterministic branch event order; events are delivered in arrival order.

Add explicit tests proving observer callback concurrency remains one while branch execution overlaps.

## Persistence and Accepted Values

Persistence remains semantic and participant-owned:

- each branch participant follows its own persistence policy;
- pipeline-level persist applies to branch participants and the group;
- a successful persisted branch records its accepted state normally;
- a successful persisted group records the merged state normally;
- branch failures, faults, and cancellation do not record accepted branch completion;
- merge failure does not record accepted group completion;
- internal helper executors never persist accepted values.

Ledger sequence reflects observation arrival, not semantic branch precedence. Merge must never depend
on journal sequence.

No SQLite schema migration is required for the first implementation if parallel membership remains
pipeline inspection metadata and branch/group accepted states retain ordinary journal records.

### Acceptance Unit of Work

Parallel agents may enter acceptance units of work concurrently. The current custom unit-of-work
contract does not declare concurrency safety.

Serialize acceptance-unit-of-work entry per run unless characterization proves an existing stronger
contract. Keep branch model execution concurrent; only accepted side-effect application and its
transactional observation are serialized.

Test re-entrancy before adding the gate so nested acceptance on one logical operation cannot deadlock.

## Inspection and Rendering

Pipeline inspection should show semantic fork/join structure without exposing clone adapters,
branch exits, or collectors.

Add non-breaking metadata to `PipelineInspection`:

```csharp
public IReadOnlyList<PipelineParallelInspection> ParallelGroups { get; init; } = [];
```

Proposed records:

```csharp
public sealed record PipelineParallelInspection(
    string Id,
    IReadOnlyList<PipelineParallelBranchInspection> Branches
);

public sealed record PipelineParallelBranchInspection(
    string Id,
    int Index,
    string ParticipantId
);
```

Mermaid and DOT should render the group as a fork and join surrounding the visible branch
participants. Branch declaration order determines rendering order. Internal physical helpers remain
hidden.

Adding an initialized property instead of another positional constructor parameter avoids breaking
existing callers that construct or deconstruct `PipelineInspection`.

## TypeScript Registration Contract

The Node registration contract gains a nested parallel node and moves to combined version 7 after integration with application-owned skills.

Conceptual shape:

```json
{
  "id": "classify-framing",
  "kind": "parallel",
  "persist": true,
  "branches": [
    { "id": "world", "participant": { "id": "world-agent", "kind": "agent" } },
    {
      "id": "epistemic",
      "participant": { "id": "epistemic-agent", "kind": "agent" }
    },
    { "id": "temporal", "participant": { "id": "temporal-agent", "kind": "agent" } }
  ],
  "mergeCallback": "c12"
}
```

The TypeScript compiler:

1. validates parallel ownership and branch kinds at construction;
2. recursively compiles each branch participant through the existing stage/agent compiler;
3. registers one synchronous merge callback;
4. parses the baseline and every branch state independently through the parent Zod schema;
5. invokes the typed merge;
6. validates and serializes the merged state through the same schema;
7. emits the nested version-six contract.

The bridge:

1. recursively creates branch `RegisteredParticipant` values;
2. creates a Core `PipelineParallel<JavaScriptState>`;
3. supplies `state => new JavaScriptState(state.Json)` as the clone function;
4. invokes the merge callback with baseline JSON plus a JSON object keyed by branch ID;
5. returns the merged `JavaScriptState`;
6. registers the composite with ordinary outcome routes and persistence policy.

Assemble branch-state JSON with `JsonDocument`/`Utf8JsonWriter`, not string concatenation.

### Contract Validation

The untrusted C# bridge validates again:

- contract version exactly 6;
- `parallel` requires at least two branches and one merge callback;
- branch IDs are nonblank and unique;
- branch participants are stage or agent only;
- nested parallel, terminal, and interaction participants are rejected;
- nested participants cannot contain parallel-only fields;
- all authored node IDs are globally unique across parent and nested participants;
- callback references are globally unique, including nested participants and merge;
- parallel is a standard-outcome route source;
- nested branch participants cannot be route endpoints, graph start, or outputs;
- persistence without a ledger path remains invalid.

Malformed nested registration is rejected before any callback or model client is created.

## Source Generator

No new source-generated stage return mode is needed. Existing generated stages already produce the
participants a parallel group owns.

Add generator-facing integration tests proving generated state, pass-through, and outcome stages can
be parallel branches. Change the generator only if those tests expose an actual binding limitation.

Parallel composition belongs in authoring, not in `[PipelineStage]` method signatures.

## Implementation Phases

### Phase 0: Pin Runtime Characterization

Add focused tests against MAF 1.16.0 before Tandem implementation:

1. Fan-out sends the same input reference to every branch.
2. Distinct branch executors overlap execution.
3. Fan-in invokes the sink once per buffered message inside one delivery batch.
4. Barrier release order is not treated as declaration order.
5. Multiple messages from one source are released in one batch when all sources contribute.
6. The barrier resets on a second occurrence.
7. A branch exception allows already-running siblings to settle but prevents downstream work.
8. Caller cancellation reaches every active branch.

Exit condition: Tandem tests encode every MAF behavior on which the feature depends.

### Phase 1: Core Semantic Model

Add `PipelineParallel`, `PipelineBranch`, merge context, ownership validation, and builder route/start
overloads. Keep the physical implementation behind internal descriptors.

Exit condition: invalid graphs fail at build time and public API boundary manifests are updated.

### Phase 2: Core Execution

Implement fork preparation, runtime cloning, branch adapters, exits, barrier collection, deterministic
runtime merge, declared failure selection, and join.

Exit condition: branches demonstrably overlap, merge is independent of completion order, repeated
visits work, and concurrent runs remain isolated.

### Phase 3: Observation, Persistence, and Inspection

Serialize observer and acceptance-unit-of-work dispatch per run, preserve branch accepted values,
persist merged state, add parallel inspection metadata, and render semantic graphs.

Exit condition: no physical helper leaks through inspection or ledger accepted values, and observer
callbacks remain serial under concurrent branch execution.

### Phase 4: TypeScript SDK and Bridge

Add the typed `parallel()` API, recursive participant compilation, combined registration contract version 7,
bridge validation, Core construction, and outcome routing.

Exit condition: TypeScript agents execute concurrently as real Tandem agents and produce one
schema-validated merged state.

### Phase 5: Documentation and Release

Add one honest C# example and one equivalent TypeScript example using independent operations. Update
README API references and coordinated TypeScript package versions.

Do not distort an existing causally sequential example merely to demonstrate parallelism.

Exit condition: package-consumer tests pass from packed artifacts and C#/TypeScript docs describe the
same semantics.

## Test Matrix

### MAF Characterization

Extend `MafBindingCharacterizationTests.cs` with the phase-zero tests above. Use task gates rather than
elapsed-time assertions.

### Core Composition

Add `ParallelPipelineCompositionTests.cs`:

- at least two branches required;
- duplicate group, branch, participant, and physical IDs rejected;
- unsupported participant kinds rejected;
- participant ownership enforced;
- group can be start or route target;
- success and failed outcome routes work;
- outer loops can revisit a group;
- generated stages and agents work as branches;
- inspection and Mermaid/DOT are semantic and deterministic;
- public API manifests contain only intended types.

### Core Runtime

Add `ParallelPipelineRunnerTests.cs`:

- branches overlap using coordination gates;
- every branch receives isolated state;
- mutable nested state is isolated when clone is correct;
- reverse completion order produces identical merge output;
- merge receives declaration order;
- merge executes exactly once;
- declared failure skips merge and selects first failure by declaration order;
- branch exception skips merge and downstream work;
- caller cancellation skips merge;
- runtime sessions, usage, counters, profiles, and latches survive merge;
- conflicting runtime deltas fail deterministically;
- repeated occurrence correlation does not retain stale results;
- concurrent runs of one built pipeline do not cross state;
- observer callbacks remain serial;
- persistence and acceptance-unit-of-work faults prevent downstream execution.

Extend `ExecutionEnvelopeInvariantTests.cs` for same-run sibling branches. Existing tests cover
parallel separate runs, not sibling executors in one run.

### Ledger and Inspection

- branch accepted states persist under branch participant IDs;
- merged accepted state persists under group ID;
- failed/cancelled/faulted branches do not create accepted completion;
- concurrent ledger sequence does not affect merged output;
- `inspectAccepted` returns ordinary branch and group values without physical helpers;
- repeated group visits create separate accepted entries normally.

### TypeScript Type Tests

- branch object preserves named state results;
- merge receives readonly branch keys;
- unknown branch access fails compilation;
- wrong-state participant fails compilation;
- terminal, interaction, and nested parallel branches fail compilation;
- async merge fails compilation;
- parallel routes require `success` or `failed` outcome;
- branch participants cannot also be parent nodes.

### TypeScript Runtime Tests

- two agent branches overlap;
- heterogeneous agent output schemas apply into independent branch states;
- merge result is validated through the parent Zod schema;
- reverse completion order does not change result keys or merged state;
- branch structured-output contract failure skips merge;
- branch agent declared failure routes through group failure;
- branch exception and caller cancellation skip merge;
- observations retain branch agent IDs and stay serial;
- branch and group persistence are inspectable;
- concurrent runs do not cross callbacks or results;
- lossy JSON, transforms, defaults, coercion, cycles, `undefined`, `NaN`, and `bigint` remain governed by
  existing boundary policy.

### Bridge Validation

- valid version-six parallel registration accepted;
- version-five registration rejected by version-six runtime;
- zero/one branch rejected;
- blank/duplicate branch IDs rejected;
- unsupported nested participant kinds rejected;
- duplicate nested node IDs rejected globally;
- duplicate callback references rejected globally;
- parallel-only fields forbidden elsewhere;
- nested branch nodes rejected as routes, start, and outputs;
- malformed registration rejected before callback invocation.

### Package Boundary

- packed C# consumer builds and runs a parallel pipeline;
- packed TypeScript consumer builds and runs an equivalent pipeline;
- non-serializable C# state works with an explicit clone;
- no internal MAF or bridge type appears in exported APIs.

## Likely Files

### C# Core

- `src/Tandem/Authoring/PipelineStep.cs`
- new `src/Tandem/Authoring/PipelineParallel.cs`
- new `src/Tandem/Authoring/ParallelPipelineNodes.cs`
- `src/Tandem/Authoring/PipelineObservationRuntime.cs`
- `src/Tandem/Domain/PipelineMessage.cs`
- `src/Tandem/ExportedApi.txt`
- `src/Tandem/PublicApiMembers.txt`

Keep new implementation out of the already large `PipelineStep.cs` except for narrow builder and
inspection integration.

### Ledger and Tooling

- `src/Tandem.Ledger/PipelineJournal.cs`, only if serialization behavior requires adjustment;
- `src/Tandem.Tool/RunInspector.cs`, only if parallel metadata is exposed there;
- terminal presentation files only if branch activity is not already represented adequately by
  ordinary step observations.

No schema migration is expected.

### TypeScript

- `typescript/packages/sdk/src/index.ts`
- `typescript/bridge/RegistrationContract.cs`
- `typescript/bridge/RegistrationContractValidator.cs`
- `typescript/bridge/RegisteredParticipants.cs`
- `typescript/bridge/RegisteredGraphBridge.cs`
- registration and participant bridge tests;
- positive and negative SDK type tests;
- runtime, observation, persistence, and JSON-boundary tests;
- generated SDK distribution and staged runtime artifacts through normal build commands.

No npm dependency is required.

## Acceptance Gates

The feature is complete only when:

- C# and TypeScript expose the same parallel-all semantics;
- branch participants remain real Tandem agents/stages;
- branches demonstrably overlap;
- application and runtime state are isolated;
- merge is deterministic across opposite completion orders;
- declared failure, exception, and cancellation behavior are distinct and tested;
- observations remain serial and identify actual branch participants;
- accepted values and persistence contain no physical helper nodes;
- repeated group visits and concurrent runs are isolated;
- MAF implementation details do not leak into public APIs;
- packed package consumers pass;
- all repository format, analyzer, unit, bridge, TypeScript, and package gates pass.

## Stop Conditions

Stop and revise the design if:

- implementation requires branch nodes to stop being normal Tandem participants;
- state merge depends on branch completion or ledger order;
- C# state must become JSON-serializable to support cloning;
- one branch can observe another branch's mutable state;
- physical helper IDs appear in public inspection, observations, or accepted history;
- custom observer or acceptance-unit-of-work implementations can be invoked concurrently without an
  explicit contract change;
- MAF barrier behavior differs from the pinned characterization tests;
- TypeScript requires a bridge-only orchestration model not representable by the C# API;
- version one expands into nested subgraphs, dynamic maps, races, quorums, or concurrent terminal
  interactions.

## Explicit Non-Goals

- `Promise.all` over arbitrary callbacks as the public abstraction;
- dynamic fan-out over a runtime collection;
- bounded parallel map;
- race/first-success;
- quorum or partial-success merge;
- arbitrary routed branch subgraphs;
- nested parallel groups;
- concurrent human interaction UI;
- implicit reflection or property-level state merging;
- rollback of external branch side effects;
- guaranteed branch observation order;
- immediate sibling cancellation after branch exception.

## Immediate Implementation Slice

1. Add the focused MAF fan-out/fan-in characterization tests.
2. Implement the C# two-branch vertical slice with explicit clone and merge.
3. Prove actual overlap, state isolation, deterministic merge, branch failure, exception, cancellation,
   observation serialization, and persistence.
4. Generalize that proven slice to N static branches.
5. Add the TypeScript API and version-six bridge over the same Core construct.
6. Use three independent agents in the first end-to-end example.

Do not begin with TypeScript callback concurrency or a Waduno integration. The C# Core contract is the
source of truth and must be proven first.
