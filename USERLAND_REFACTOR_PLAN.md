# Tandem Public SDK Refactor

## Objective

Tandem provides one progressive pipeline-authoring SDK over MAF.

An ordinary author describes durable state, prompts, policies, capabilities,
meaningful results, and routes. Tandem owns orchestration, execution envelopes,
durability, sessions, usage, agent loops, tool dispatch, suspension, replay, and
MAF integration.

Advanced block authors may use Tandem's block-level concepts when implementing
block behavior. The goal is to remove accidental execution plumbing, not to
pretend that a block SDK has no block model.

The completed SDK must support four progressively richer journeys without
introducing separate programming models:

- Songwriter proves the minimal pipeline journey.
- Support adds agents and durable request/response handoff.
- Debate adds loops, sessions, lifecycle actions, and teardown policy.
- Delivery adds custom blocks, workspaces, checkpoints, tools, observations,
  and human handoff.

## Fixed Invariants

### One Composition Model

All pipelines use the same fluent graph composition API. Advanced pipelines add
capabilities and policies; they do not replace the authoring model.

Fluent call order does not implicitly create graph edges. Every successor is
declared through `Route`.

### State Ownership

`TState` is the author's durable pipeline state.

Ordinary steps and ordinary author policies receive `TState`, not the complete
execution envelope. They return either unchanged execution, updated state, a
standard Tandem outcome, or a custom semantic result.

Facts needed by later semantic decisions belong in `TState`. Operational
evidence may remain in a block outcome. A generic outcome payload must not be
used as hidden application state.

### Execution Ownership

Tandem exclusively transports and preserves:

- run identity;
- agent sessions;
- usage accounting;
- invocation counts;
- selected profiles;
- latest block outcome;
- latest routing result;
- persistence and replay metadata; and
- observations.

User code must never copy these fields merely to keep execution working.

Generated adapters and runtime operations preserve the execution envelope even
when authored code returns only state or a semantic result.

### Block Outcomes

`BlockOutcome` is legitimate Tandem SDK vocabulary. It records what a block did
and may be used by custom blocks, lifecycle policies, observations, diagnostics,
and downstream infrastructure that genuinely needs general block evidence.

`BlockOutcome` is not mandatory baggage for authored result cases. A result case
may include one when it is meaningful branch data; the generator must never
require it to reconstruct the execution envelope.

Stable string outcome identifiers remain valid durable protocol identifiers.
They must be owned by constants or an equivalent vocabulary rather than
scattered literals. They are not replaced by CLR type names.

### Framework Boundary

No public Tandem API or consumer project exposes MAF types.

Ordinary authors do not receive infrastructure persistence APIs. Advanced
block-level APIs may expose Tandem abstractions for persistence, observation,
and block execution when those capabilities are the purpose of the extension.

### Failure Categories

A declared Tandem `Failed` outcome is recoverable pipeline data when routed.

An unhandled declared `Failed` outcome ends the run with failed status.

An exception is an undeclared execution fault, not a declared `Failed` outcome.

Cancellation is cancellation, not failure.

An explicit run-termination semantic may be introduced separately if required;
it must not be simulated through exceptions or arbitrary outcome strings.

## Step Return Contract

The source generator infers the authoring mode from `ExecuteAsync`. Authors do
not configure a mode or provide explicit generic arguments to select one.

### Pass-Through Step

```csharp
ValueTask ExecuteAsync(TState state, CancellationToken cancellationToken)
```

Normal completion produces Tandem's standard `Success`. The current state is
preserved. The step has no result selectors.

### State-Updating Step

```csharp
ValueTask<TState> ExecuteAsync(TState state, CancellationToken cancellationToken)
```

Normal completion produces Tandem's standard `Success` carrying the returned
state. The step has no result selectors.

### Standard-Outcome Step

```csharp
ValueTask<Outcome<TState>> ExecuteAsync(
    TState state,
    CancellationToken cancellationToken
)
```

The step explicitly returns Tandem's typed `Success` or `Failed`. The generated
step exposes corresponding typed selectors.

### Custom-Result Step

```csharp
ValueTask<ReviewResult> ExecuteAsync(
    TState state,
    CancellationToken cancellationToken
)
```

`ReviewResult` is an authored Dunet union. It replaces the standard authored
result for that step and exposes only its declared cases as typed selectors.
Tandem still records the invocation's internal execution disposition.

Custom unions are used only when userland has additional outcomes worth naming,
such as `Accepted`, `ChangesRequested`, or `NeedsHuman`.

### Static Guarantees

The generator must reject unsupported or ambiguous signatures with actionable
diagnostics.

The generated API must ensure:

- a pass-through or state-updating step has no `.Result` selectors;
- a standard-outcome step exposes only `Success` and `Failed`;
- a custom-result step exposes only declared Dunet cases;
- routes cannot reference a case belonging to another step or state type; and
- no `object`, `dynamic`, generic JSON matching, or runtime reflection is needed
  for ordinary routing.

## Routing Contract

### Unconditional Output Route

```csharp
.Route(on: prepareWorkspace, to: execute, label: "workspace prepared")
```

This means the source step produced an output, regardless of which output it
produced. It does not fire after an exception, cancellation, or explicit run
termination.

### Result-Specific Route

```csharp
.Route(on: review.Result.Accepted, to: complete, label: "accepted")
```

This fires only for the selected standard or custom result case.

### Route Exclusivity

An unconditional route and result-specific routes from the same source would
both match the same output and create accidental fan-out. Tandem must reject
mixing those route modes for a source unless explicit parallel fan-out is added
as a separate supported feature.

Multiple result-specific routes are valid. Multiple conditional routes for the
same case remain valid when their predicates are deliberately exclusive.

### Unhandled Failure

If a standard `Failed` result has no matching route, Tandem ends the run with
failed status and preserves the failure evidence.

If `Failed` has a matching result-specific or unconditional route, the pipeline
continues through that route.

## Agent Contract

Agents participate in the same step return model; they are not a separate
composition system.

An agent operation receives authored state. Tandem supplies the active execution
envelope internally and preserves runtime changes after the model call, tools,
session policy, usage accounting, and teardown.

An agent may use Tandem's standard outcome:

```csharp
return await operation.RunAsync(state, cancellationToken);
```

Or map the agent's meaningful block result into an authored custom union:

```csharp
return await operation.RunAsync(
    state,
    result => result.Kind switch
    {
        OutcomeKinds.Accepted => new ReviewResult.Accepted(result.State),
        OutcomeKinds.ChangesRequested =>
            new ReviewResult.ChangesRequested(result.State),
        _ => new ReviewResult.Unexpected(result.State),
    },
    failure => new ReviewResult.Failed(state, failure),
    cancellationToken
);
```

The mapped result exposes state and legitimate block outcome information, but
not `PipelineRuntime`, MAF types, sessions, usage dictionaries, or the complete
pipeline envelope.

Custom result mappings must handle agent infrastructure failure explicitly. A
failure must not be collapsed into a successful or unrelated semantic case.
Pipelines route that failure case to a typed terminal failure node:

```csharp
var failed = PipelineNodes.Failed<TState>("failed");
```

Agent construction is per pipeline build because update callbacks and execution
observers are build-specific. Dependency injection stores stable dependencies,
not operations that capture a previous build context.

Session policy remains explicit. Tandem must not guess retain/reset/teardown
behavior from a step name.

## Request Contract

Durable request handoff is a composed Tandem capability. Userland supplies:

```csharp
Func<TState, TRequest> createRequest
Func<TState, TResponse, TState> applyResponse
```

Tandem saves and restores the complete execution envelope, applies the authored
state transition, records resume evidence, and preserves runtime metadata.

Userland never serializes, restores, or reconstructs `PipelineMessage<TState>`
for an ordinary request.

## Advanced Block Contract

Advanced block authors may intentionally work with:

- `PipelineMessage<TState>`;
- `BlockOutcome`;
- stable outcome kinds;
- block execution observation;
- Tandem persistence abstractions; and
- lifecycle/session policy observations.

An advanced generated wrapper may declare
`ExecuteAsync(PipelineMessage<TState>, CancellationToken)` when its purpose is
adapting an envelope-aware custom block into typed routes. This is an explicit
advanced form, not the ordinary state-first authoring contract.

This is allowed only where the code is implementing block or runtime policy.
Ordinary prompts, parsers, state transitions, and route predicates should use
`TState` unless they demonstrate a need for execution context.

Delivery remains the acceptance consumer for this layer. Its use of block
outcomes is not removed mechanically. Each use is evaluated by purpose.

## Implementation Sequence

### 1. Generator Model

- Recognize the four approved `ExecuteAsync` return forms.
- Recognize ordinary `TState` input.
- Emit separate adapters for pass-through, state update, standard outcome, and
  custom Dunet result.
- Preserve the execution envelope in every adapter.
- Emit actionable diagnostics for invalid signatures and result cases.
- Remove the requirement that every step declare a nested Dunet union.

### 2. Standard Outcomes

- Add the public typed Tandem `Outcome<TState>` contract.
- Add standard `Success` and `Failed` values with durable outcome identities.
- Keep declared failure separate from exceptions and cancellation.
- Record enough failure evidence for durable status and observation.

### 3. Typed Routing

- Add the approved `.Route(on: step, to: next)` overload.
- Generate `Success`/`Failed` selectors only for standard-outcome steps.
- Preserve custom Dunet selectors for custom-result steps.
- Track route mode per source and reject unconditional/result-specific mixing.
- Evaluate conditional failure predicates against the failed state; a declared
  route handles failure only when its predicate matches.
- Ensure an unhandled `Failed` produces failed run status.

### 4. Envelope Transport

- Make generated execution own the active envelope.
- Make agent and advanced operation execution update that owned envelope.
- Remove authored runtime/outcome copying.
- Replace temporary or misleading abstractions such as deterministic operations
  returning an `AgentResult`.
- Prove nested, concurrent, cancelled, and faulted executions cannot leak or
  cross-contaminate envelope state.

### 5. State-First Authoring

- Convert ordinary stages to `TState` input.
- Convert ordinary prompts and structured-output parsers to state-first
  callbacks where no runtime context is used.
- Convert route predicates to state-first callbacks.
- Retain explicit advanced context variants where execution information is
  genuinely required.

### 6. Agent Integration

- Make `AgentOperation.RunAsync` accept authored state.
- Support standard outcomes and mapped custom Dunet results.
- Keep block evidence available to meaningful mappings.
- Keep sessions, usage, profiles, lifecycle receipts, and teardown internal to
  envelope propagation.
- Keep operations scoped to `PipelineBuildContext`.

### 7. Durable Requests

- Keep request creation and response application state-first.
- Verify suspension persists the complete envelope.
- Verify resume changes state without losing runtime, outcomes, usage, sessions,
  or routing identity.

### 8. Progressive Consumers

Songwriter must demonstrate:

- pass-through or state-updating simple stages without one-case unions;
- agent-backed custom outcomes only where branching requires them;
- unconditional routes for serial flow; and
- no workspace, lifecycle, or runtime plumbing.

Support must additionally demonstrate:

- typed state transitions;
- account lookup through a consumer-owned port;
- durable typed request/response handoff; and
- close/escalate routing after resume.

Debate must additionally demonstrate:

- revision loops;
- explicit agent session policy;
- lifecycle actions and receipts; and
- teardown policy using legitimate block evidence.

Delivery must additionally demonstrate:

- custom block implementations;
- workspace capabilities and mutation policy;
- verification commands;
- checkpoints;
- planner/reviewer lifecycle outcomes;
- human handoff; and
- observations.

Delivery's human-answer source must move out of `LatestOutcome.Payload` if it is
required for later semantic routing; that fact belongs in durable state.

### 9. Documentation And Contract Record

- Document the four inferred step forms.
- Document unconditional versus result-specific routing.
- Document declared failure, faults, cancellation, and recovery.
- Document ordinary authoring versus advanced block authoring.
- Copy examples from compiled Songwriter, Support, Debate, and Delivery code.
- Do not retain hypothetical APIs after implementation.

## Verification

### Generator Tests

Prove valid generation for all four return forms and diagnostics for:

- unsupported input types;
- unsupported return types;
- malformed custom unions;
- result cases without state where state is required;
- selectors for undeclared cases; and
- incompatible state types.

### Routing Tests

Prove:

- unconditional routes accept any produced result;
- custom cases select only matching routes;
- standard `Success` and `Failed` select matching routes;
- unconditional and case-specific modes cannot be mixed accidentally;
- routed failure recovers;
- unhandled failure ends with failed status; and
- exceptions and cancellation do not follow ordinary routes.

### Envelope Tests

Prove:

- pass-through preserves state and runtime;
- state-returning steps replace only state;
- agents preserve session, usage, profile, invocation, and outcome changes;
- custom result adaptation preserves the updated envelope;
- nested execution restores the previous scope;
- parallel runs do not share envelope state;
- cancellation clears execution scope; and
- exceptions clear execution scope.

### Durability Tests

Prove:

- custom routing survives serialization and restart;
- standard outcomes survive serialization and restart;
- Support suspends, restores, applies a response, and completes;
- resumed runs retain pre-suspension runtime metadata; and
- unhandled failure status is durable.

### Boundary Tests

Mechanically enforce:

- public Tandem APIs expose no MAF types;
- consumer projects import no MAF namespaces;
- ordinary authored results do not carry runtime fields by requirement;
- generated adapters do not discover magic `Runtime` or `Outcome` property
  names;
- ordinary state callbacks do not require `PipelineMessage<TState>`; and
- advanced block APIs remain available without leaking infrastructure types.

### Repository Gate

Run:

```bash
task check
git diff --check
~/Sites/plumb/plumb . --json
```

All tests, formatting, analyzers, and builds must pass. Meridian error findings
must be fixed. Warnings must be fixed or documented as inapplicable.

## Non-Goals

This refactor does not:

- remove `BlockOutcome` from all first-party or consumer code;
- replace durable outcome identifiers with CLR type names;
- force every custom union to redeclare `Success` and `Failed`;
- add a generic typed-outcome registration framework;
- expose MAF to make advanced scenarios easier;
- infer session policy from agent names;
- add arbitrary tools without explicit capability and policy semantics;
- add implicit graph edges based only on fluent call order;
- add parallel fan-out semantics; or
- preserve compatibility with the superseded greenfield authoring API.

## Completion Criteria

The refactor is complete when a new author can read Songwriter and understand
the minimal Tandem model without learning execution envelopes; Support, Debate,
and Delivery extend that exact model rather than replacing it; declared failure,
faults, cancellation, routing, persistence, and resume have explicit tested
semantics; advanced block authors retain the block concepts they need; no
consumer transports runtime metadata merely to keep execution working; and the
repository gate passes.
