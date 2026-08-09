# TypeScript SDK Stabilization Plan

Status: items A-D implemented. Stability verification is recorded by the repository
test and package gates. Declared stage outcomes remain trigger-gated. Runtime
observation and inspection are separate later designs, not part of the immediate
authoring tranche.

## Principles

Preserve Tandem's governing boundaries:

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

TypeScript owns application meaning. Tandem and MAF remain authoritative for graph
execution, routing order, agent loops, interaction suspension, correction,
acceptance, observation ordering, and persistence semantics. Do not turn this plan
into mechanical C# parity work.

## Delivery Order

```text
A. JSON-lossless boundaries
B. Opaque callback registry
C. Authored instructions and contextual validation
D. Per-run interaction handlers
E. Stability checkpoint
F. Stage outcomes only after a real trigger
G. Runtime observation design
H. Graph inspection design
I. Accepted-history design
```

Items A-D happen before stabilizing the public TypeScript API.

## A. JSON-Lossless Boundaries

### Decision

Keep ordinary Zod schemas. Add no public `JsonValue`, serializable-state wrapper,
serializer hook, or transport configuration. JSON losslessness is a private
TypeScript adapter constraint, not a restriction on ordinary C# `TState`.

### Implementation

Add one private operation equivalent to:

```ts
function serializeBoundary<T>(schema: z.ZodType<T>, value: unknown, boundary: string): string;
```

It must:

1. Validate through Zod.
2. Reject coercion, stripping, defaults, or transforms.
3. Serialize exactly once.
4. Reject a top-level `undefined` result.
5. Parse the produced JSON.
6. Compare the parsed value with the validated input.
7. Return the already-produced JSON string.

Use it for every JavaScript-to-bridge value:

- initial state;
- stage state;
- interaction request and response;
- interaction-applied state;
- capability-applied state; and
- output-applied state.

Bridge-originated JSON continues through the existing `parseJson` path.

Reject `NaN`, infinities, `bigint`, undefined properties and array entries, sparse
arrays, `Date`, lossy class instances, symbol or non-enumerable state, effective
`toJSON` mutation, and cycles.

### Acceptance

- Invalid values fail as `ContractValidationError` before crossing the bridge.
- Errors identify the active semantic boundary.
- Valid JSON survives Node to C# to Node unchanged.
- Existing Zod validation-only behavior remains intact.
- Ordinary C# state remains unconstrained by JSON serialization.

## B. Opaque Callback Registry

### Decision

Callback IDs remain private and run-scoped. Authored participant IDs have no role in
callback identity.

### Implementation

Replace distributed callback key construction with one private allocator:

```ts
const callbacks = createCallbackRegistry();
const message = callbacks.registerSync(callback);
const execute = callbacks.registerAsync(callback);
```

Use one monotonic namespace across synchronous and asynchronous callbacks:

```text
c0
c1
c2
```

The registry owns allocation, registration, lookup, invocation, missing-ID errors,
callback result envelopes, and reference lifetime.

The bridge registration validator rejects duplicate callback references. Do not
restore a top-level callback manifest.

### Acceptance

- Authored node IDs never appear in callback IDs.
- Every registration receives a unique callback ID.
- Concurrent runs cannot cross-dispatch callbacks.
- Missing and duplicated references fail deterministically.
- References are released after success, declared failure, fault, and cancellation.
- Existing callback failure and cancellation translation remains unchanged.

No registration version bump is needed while callback fields remain opaque strings.

## C. Authored Instructions And Contextual Validation

Implement instructions and contextual validation together because they complete
application ownership of output and capability contracts.

### Authored Instructions

Make instructions required:

```ts
capability({
  name: "submit_implementation",
  instructions: "Submit the complete implementation and rationale.",
  schema: Submission,
  summarize,
  apply,
});
```

```ts
output: {
  instructions: "Return Accept or RequestChanges with concrete findings.",
  schema: ReviewDecision,
  apply: recordReview,
}
```

Agent instructions describe the participant's role. Output and capability
instructions describe their local machine boundary. Remove bridge-generated
`Invoke ${name}.` and `Return the requested structured value.` text.

This is a deliberate pre-stability source break. Do not preserve optional generic
fallback instructions.

### Contextual Validation

Zod remains intrinsic validation. Add one optional synchronous state-aware callback:

```ts
type ValidationProblem = {
  readonly path: string;
  readonly message: string;
};

validateFor?: (
  state: TState,
  value: TValue,
) => readonly ValidationProblem[];
```

Use `validateFor`, not another generic `validate`, because Zod already owns intrinsic
shape validation.

Core remains authoritative for this order:

1. Parse JSON.
2. Run intrinsic Zod validation.
3. Run `validateFor(state, value)`.
4. Correct invalid model output or capability requests.
5. Summarize.
6. Persist acceptance.
7. Apply the state transition.

Intrinsic failure short-circuits contextual validation. Keep contextual validation
synchronous until a demonstrated use case earns asynchronous cancellation and
correction semantics.

### Acceptance

- Agent, output, and capability instructions are required and nonblank.
- Exact authored instructions reach provider requests.
- Intrinsic failure does not invoke `validateFor`.
- Context-invalid output enters Core correction.
- Context-invalid capability returns a structured correctable tool error.
- Validation paths survive TypeScript, bridge, and Core.
- `apply` runs once and only after both validation layers and acceptance.
- Validator exceptions remain undeclared faults.

## D. Per-Run Interaction Handlers

### Decision

Move the concrete host channel out of the interaction participant.

The graph owns semantic identity, request and response contracts, request projection,
and response transition:

```ts
const review = interaction({
  id: "review",
  requestSchema: ReviewRequest,
  responseSchema: ReviewResponse,
  request: createReviewRequest,
  apply: recordReview,
});
```

The run owns the handler:

```ts
const handlers = interactions().handle(review, async (request, { signal }) =>
  askReviewer(request, signal),
);

await run(graph, initialState, {
  interactions: handlers,
  signal,
});
```

Recommended public shape:

```ts
interface InteractionHandlers {
  handle<TState, TRequest, TResponse>(
    interaction: Interaction<TState, TRequest, TResponse>,
    handler: (
      request: TRequest,
      context: { readonly signal: AbortSignal },
    ) => TResponse | Promise<TResponse>,
  ): InteractionHandlers;
}
```

Do not initially expose run ID, request ID, or bridge context. The typed binding
already identifies the interaction; promote runtime context only after a concrete
host correlation requirement appears.

### Bridge Work

- Keep request and apply callbacks on interaction registration.
- Remove the handler callback from the graph node.
- Carry run-local interaction handler callbacks in run options.
- Continue constructing real C# `PipelineInteractionHandlers`.
- Keep suspension and resumption in MAF.

### Acceptance

- One graph can run with different concrete handlers.
- Concurrent runs own isolated handler registries.
- Duplicate handler registration fails before execution.
- Foreign interaction registration fails before execution.
- A missing handler fails only when its interaction is reached.
- Handler responses are validated before application.
- Cancellation prevents late response application.
- Runtime identities never enter `TState`.

This is a deliberate public break. Do not retain embedded handlers as a compatibility
shim because that preserves the wrong ownership and creates precedence ambiguity.

## E. Stability Checkpoint

After A-D:

- dogfood one graph with two different interaction handlers;
- rerun concurrent, cancellation, failure, and packed-consumer tests;
- rerun callback-reference and lifecycle soak;
- run a full Meridian review;
- review final names and type inference; and
- decide whether the Core TypeScript authoring surface is ready for experimental
  publication.

Do not add broader parity work before this checkpoint.

## F. Trigger-Gated Declared Stage Outcomes

### Decision

Do not implement declared ordinary-stage outcomes now.

The current sample's verification result is application evidence used by later
participants and routes. It correctly belongs in state. Turning verification
pass/fail into execution success/failure would violate Tandem's model.

### Trigger

Implement only when a real TypeScript sample contains an ordinary operation where:

- expected operational failure differs materially from success;
- failure needs structured `FailureEvidence`;
- composition has a recovery, remediation, or deliberate failure route;
- the result is not a domain decision another participant needs as a state fact; and
- thrown exceptions remain a distinct undeclared-fault category.

A qualifying example is artifact deployment where rejection routes to remediation,
while callback bugs still fault the run.

### Eventual Shape

```ts
type FailureEvidence = {
  readonly code: string;
  readonly summary: string;
  readonly detail?: string;
};

type StageOutcome<TState> =
  | { readonly outcome: "success"; readonly state: TState }
  | {
      readonly outcome: "failed";
      readonly state: TState;
      readonly failure: FailureEvidence;
    };
```

Use an explicit standard-outcome stage mode. Do not infer it structurally and do not
support custom outcome names.

When triggered, the work includes:

- a supported Core dynamic outcome-stage factory;
- opaque `OutcomeStage<TState>` typing;
- success and failed stage routes;
- rejection of mixed default and outcome-specific routing;
- a private callback outcome envelope;
- a registration contract version bump;
- bridge mapping to existing C# `Outcome<TState>`; and
- persistence tests for successful state and `FailureEvidence`.

Until the trigger exists, this remains design documentation rather than backlog
implementation.

## G. Later Runtime Observation Design

### Ownership

Use one optional, awaited observer under `run`:

```ts
await run(graph, initialState, {
  observe: async (event, { signal }) => {
    // Read-only host observation.
  },
});
```

`run()` remains the sole lifecycle and completion authority. Do not add a run
controller, lifecycle commands, a second event-driven coordinator, an unbounded
`EventEmitter`, or an async iterator as the primary API.

### Initial Event Set

The first version should contain only:

- step started;
- step completed;
- step cancelled;
- step faulted;
- agent text;
- agent reasoning; and
- normalized agent usage.

Exclude generic tool traffic, command output, workspace mechanics, invocation IDs,
interaction request IDs, raw MAF/provider objects, and acceptance payloads.

### Delivery Rules

- Delivery is serial and awaited.
- Backpressure remains constant-space.
- A slow observer slows only its run.
- No callback runs after `run()` settles.
- Each run retains only its own callback.
- Concurrent runs cannot cross-deliver.
- Observer failure faults the run unless another authoritative failure already exists.

### Critical Prerequisite

Do not compose live observation naively into durable acceptance observation.
Persistence currently participates in the acceptance transaction. A logger or UI
failure must not roll back durable semantic acceptance.

Before acceptance events are exposed, Core must deliberately separate:

```text
Durable acceptance observation inside the transaction.
Live host observation after committed acceptance.
```

This is a separate later design and runtime change.

## H. Later Graph Inspection Read Model

Expose a separate versioned semantic projection:

```ts
const inspection = await inspectGraph(pipeline);
```

```ts
interface PipelineGraphInspection {
  readonly contractVersion: 1;
  readonly name: string;
  readonly description: string | null;
  readonly startStepId: string;
  readonly stepIds: readonly string[];
  readonly interactions: readonly InteractionInspection[];
  readonly routes: readonly RouteInspection[];
  readonly outputStepIds: readonly string[];
  readonly persistentStepIds: readonly string[];
  readonly renderings: {
    readonly mermaid: string;
    readonly dot: string;
  };
}
```

The result must come from authoritative C# `Pipeline.Inspect()` after building the
real Tandem graph. Do not reconstruct it from TypeScript declarations.

Prerequisites:

1. Settle C# route-order projection; current inspection sorting does not represent
   evaluation precedence.
2. Factor graph construction so execution and inspection share one path.
3. Ensure inspection performs no provider preflight, callback invocation, ledger
   creation, or run creation.
4. Version graph projection independently of registration and ledger contracts.

Do not add graph pagination, JavaScript Mermaid/DOT rendering, MAF executor IDs,
registration JSON, or inferred node kinds that C# does not project semantically.

## I. Later Accepted-History Read Model

Keep accepted history separate from graph inspection:

```ts
const page = await inspectAcceptedHistory({
  ledgerPath,
  runId,
  afterSequence,
  limit,
  stepId,
  valueType,
  kinds,
});
```

```ts
interface AcceptedHistoryPage {
  readonly contractVersion: 1;
  readonly run: {
    readonly runId: string;
    readonly composition: string;
    readonly status: RunStatus;
  };
  readonly items: readonly AcceptedHistoryItem[];
  readonly nextSequence: number | null;
  readonly hasMore: boolean;
}
```

Each item includes sequence, timestamp, accepted kind, step ID, identity, semantic
name, value type, result, outcome kind, and payload.

Payload remains `unknown | null` because historical inspection must work without
loading original application schemas. Provide explicit application-owned decoding:

```ts
decodeAcceptedPayload(item, schema);
```

Never infer decoding from descriptive `valueType` metadata.

### Authority And Pagination

C# owns SQLite access, stream contract validation, accepted classification, sequence
ordering, run status, filtering, pagination, and projection. Replace bridge-local
unbounded SQL with a focused bounded reader in `Tandem.Ledger`.

Use sequence cursors only:

```text
sequence > afterSequence
ORDER BY sequence ASC
```

Apply filters before page truncation. Sequences may contain gaps. Do not add offset
or timestamp pagination.

### Non-Goals

- no general ledger browser;
- no operational journal API;
- no resume or workflow reconstruction;
- no event-sourced application state;
- no automatic payload registry; and
- no SQLite dependency in `@tandem/sdk`.

## Work Packages

### Package 1: Boundary Hardening

- JSON-lossless serialization;
- opaque callback registry; and
- contract, concurrency, and lifetime tests.

### Package 2: Semantic Contracts

- required output and capability instructions;
- synchronous contextual `validateFor`; and
- provider protocol, correction, and acceptance tests.

### Package 3: Interaction Ownership

- per-run typed interaction handlers;
- removal of embedded handlers; and
- concurrent-channel and cancellation tests.

### Stability Review

- complete dogfood;
- soak and package proof;
- Meridian review; and
- explicit API stability decision.

### Deferred Design Records

- standard stage outcomes and their trigger;
- live runtime observation;
- graph inspection; and
- accepted-history inspection.

## Approved Decisions

1. Enforce JSON losslessness privately without a public JSON state type.
2. Use opaque per-run monotonic callback IDs.
3. Require authored output and capability instructions.
4. Add synchronous `validateFor` and defer asynchronous validators.
5. Move interaction handlers entirely to `run()` without a compatibility shim.
6. Keep handler context to typed request plus `AbortSignal`; defer runtime IDs.
7. Defer declared stage outcomes until a real operational-failure sample earns them.
8. Design observation as awaited `RunOptions.observe`, starting with lifecycle,
   text, reasoning, and usage.
9. Keep graph and accepted history as separate versioned read models.
10. Keep accepted payload decoding explicit and application-schema-owned.

## Verification Baseline

Every implementation package must preserve:

- TypeScript positive and negative compilation tests;
- real Node to CoreCLR to Tandem integration tests;
- normal Tandem `task check`;
- package-consumer tests for public Core changes;
- local-tarball package-relative execution;
- SQLite acceptance and rollback proofs;
- concurrent-run and cancellation isolation;
- callback failure propagation; and
- Meridian Plumb with no unresolved error-level findings.
