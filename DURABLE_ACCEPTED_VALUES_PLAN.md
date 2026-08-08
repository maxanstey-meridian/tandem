# Durable Accepted Values Plan

Status: implemented; pending review and commit.

## Objective

Make a persistent Tandem pipeline a durable history of its accepted authored
values without requiring application callbacks whose only purpose is to save those
values.

Tandem already knows the step, run, invocation, concrete type, serializer,
accepted identity, payload, outcome, and ordering at each machine boundary. The
runtime currently discards accepted payloads while projecting observations into
SQLite. This campaign carries those payloads through the existing observation and
journal path.

The governing rule is:

```text
If a value crosses a declared semantic pipeline boundary successfully,
and persistence resolves on for that step, it appears in the run ledger.
```

This is not workflow resume, `TState` persistence, event sourcing, or a second
application record framework.

## Settled Product Decisions

1. Persistence is explicit authored pipeline policy.
2. `.Persist()` on `PipelineBuilder<TState>` enables accepted-value persistence
   for the pipeline by default.
3. `.DoNotPersist()` keeps or restores an ephemeral pipeline default.
4. `.Persist(step)` and `.DoNotPersist(step)` override the default for one semantic
   pipeline participant.
5. The step override wins; otherwise the pipeline default applies.
6. A persistent pipeline fails before its first step when the host has no durable
   persistence observer. Persistence is never best-effort.
7. Existing output records, capability requests, interaction values, validators,
   definitions, and state transitions require no persistence interfaces or mapping.
8. Tandem automatically persists accepted values. `WithOutputAcceptance(...)` and
   capability `.WithAcceptance(...)` remain only for genuine authoritative I/O,
   derived facts, or external consistency boundaries.
9. Operational telemetry and accepted semantic history remain distinct views over
   one run.
10. Persistence policy is immutable built-pipeline metadata, never `TState` or
    mutable runtime bookkeeping.

## Confirmed Current Seams

The following claims are verified against current implementation rather than
inferred from the design:

### Pipeline Metadata

- `PipelineBuilder<TState>` owns composition until one `Build(...)` call.
- `Pipeline<TState>` is immutable after construction and already carries output,
  route, and interaction metadata alongside the MAF workflow.
- `PipelineRunner` receives both the built pipeline and `PipelineRunOptions`, so it
  is the honest preflight point for a persistence requirement.
- One built pipeline is reusable across concurrent run IDs; policy must therefore
  remain immutable on `Pipeline<TState>`.

The minimal authored shape is:

```csharp
var pipeline = Pipeline
    .Start(planner, "delivery")
    .Persist()
    .DoNotPersist(credentialsAgent)
    .Route(...)
    .Build(complete);
```

The node overload uses existing `IPipelineNode` identity. For an interaction, the
overload accepts its semantic `PipelineInteraction<...>` and applies one policy to
its request, port, and resume mechanics. Do not add output-, capability-, or
interaction-specific persistence definition hierarchies in this campaign.

### Host Durability Capability

`PipelineRunOptions` currently exposes only an arbitrary `IPipelineObserver`; an
observer is not proof of durable storage. The Tool combines a
`LedgerPipelineObserver` with a dashboard observer and separately supplies the
SQLite-backed acceptance UOW.

Add one narrow host SPI in Core:

```csharp
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPipelinePersistenceObserver : IPipelineObserver;
```

This interface exists because `Tandem.Tool` is a separate assembly while Core must
not reference `Tandem.Ledger`. It is host runtime plumbing, not an application
authoring concept. `LedgerPipelineObserver` implements it. The Tool's composite
observer used for runs also implements it because it always contains that ledger
observer.

`PipelineRunner` rejects a pipeline with any resolved persistent step unless
`PipelineRunOptions.Observer` implements this SPI. The existing acceptance UOW
continues to provide the SQLite transaction used while outputs and capabilities
are accepted.

### Structured Outputs

After intrinsic, contextual, and synchronous acceptance, Core already holds:

```csharp
structuredResult.Outcome.Payload
config.StructuredOutput.OutputType
acceptedOutputId
```

The payload was produced with the same immutable Web JSON options used for schema,
examples, deserialization, and accepted output. `AgentBlock` currently emits
`PipelineStructuredOutputAccepted` without payload or type and then maps state.

Enrich that observation with:

```text
AcceptedOutputId
OutputType (descriptive CLR full name)
OutcomeKind
Payload
```

Emit it only after all authored acceptance succeeds and before state mapping, as
today. Rejected and corrected-away candidates never produce this observation.

### Capabilities

`CapabilityFunction<TState,TRequest>` already serializes the validated request to
`payload` using its existing `_jsonOptions` before reservation. It also has
`typeof(TRequest)`, capability ID/name, invocation ID, and the exact accepted-call
identity derivable as:

```text
{RunId:N}:{BlockId}:{InvocationId}:{CapabilityId}
```

Enrich `PipelineCapabilityAccepted` with the accepted-call ID, request type, and
the already serialized payload. Do not serialize the request again in the
observer.

### Interactions

`PipelineInteractionRequested<TRequest>` and
`PipelineInteractionAnswered<TResponse>` already carry the typed values. The
current ledger observer switches on their non-generic base classes and discards
those values.

Add serialized payload to the base observation contracts when the generic
interaction creates them. Use the existing authored request/response types and Web
JSON defaults; do not reflect over the generic observation inside the Tool.

Current ordering is:

```text
request observation -> host handler
host response -> send response into MAF -> answered observation
```

Move the answered observation before `run.SendResponseAsync(...)`. A persisted
response must exist before the live pipeline can consume it. The request
observation already occurs before the handler sees the request.

### Stages And Terminals

Generated stages currently return only:

```text
ValueTask
ValueTask<TState>
ValueTask<Outcome<TState>>
```

There is no independent typed stage-result value to archive without persisting
`TState`. Do not invent one in this campaign.

`PipelineStepCompleted` already carries a `PipelineRunOutcome` with a JSON payload.
Persist that payload only for declared standard failure evidence. Agent success and
capability outcome payloads duplicate their dedicated accepted-value records;
successful state-only stages and current terminal definitions generally emit `{}`.
No `TState` snapshot or fictional terminal value is added.

`PipelineCommandOutput` contains potentially large stdout/stderr. Keep the current
metadata-only runtime journal behavior; Delivery's validated `VerificationResult`
remains its authoritative semantic record.

## Persistence Policy Resolution

Use one small internal enum:

```text
Inherit
Persist
DoNotPersist
```

`PipelineBuilder<TState>` carries:

```text
pipeline default                         Persist | DoNotPersist
participant overrides keyed by node ref Persist | DoNotPersist
```

At build time:

1. Validate every overridden node or interaction participates in the built graph.
2. Collapse interactions to their semantic ID.
3. Store the pipeline default and semantic step-ID overrides on `Pipeline<TState>`.
4. Include resolved persistence in `PipelineInspection` so the retention contract
   is visible before execution.

`WorkflowRunner` passes the immutable policy into `PipelineRunContext` when it
creates the initial `PipelineMessage<TState>`. The run context answers whether a
semantic step is persistent. Accepted observations include payload only when that
answer is true; metadata-only runtime observations may still flow to ordinary
observers.

If no step resolves persistent, no durable observer is required and execution
remains unchanged.

## Durable Journal Shape

Extend `RuntimeJournalRecord` additively:

```csharp
string? ValueType = null,
JsonElement? Payload = null
```

The runtime journal remains one persisted contract:

```text
storage name:    runtime.journal
contract name:   tandem.runtime-journal
contract version: 1
```

Optional additive fields preserve readability of existing rows. Historical
inspection treats `ValueType` as descriptive metadata and pretty-prints raw JSON;
it must not require loading the original CLR type or deserializing old payloads
into renamed types.

For accepted values, stop using only process-local sequential entry IDs. Use
deterministic run-unique entry identities so acceptance retries converge:

```text
accepted-output--{AcceptedOutputId}
accepted-capability--{AcceptedCallId}
interaction-request--{RequestId}
interaction-response--{RequestId}
```

Other operational journal records may retain sequential `runtime--{sequence}`
entry IDs. SQLite still allocates the stream sequence atomically and preserves one
chronological stream.

`LedgerPipelineObserver` maps enriched observations directly into the extended
record. There is no application mapping callback between an accepted value and
its journal row.

## Runtime Ordering

Structured output remains:

```text
deserialize
    -> intrinsic validation
    -> contextual validation
    -> synchronous acceptance policy
    -> authored Advanced acceptance, when present
    -> append accepted typed payload to runtime journal
    -> cancellation check
    -> apply TState transition exactly once
    -> publish and route
```

Capability acceptance remains:

```text
deserialize request
    -> intrinsic validation
    -> contextual validation
    -> summarize and serialize once
    -> reserve lifecycle transition
    -> authored Advanced acceptance, when present
    -> append accepted typed payload to runtime journal
    -> cancellation check
    -> apply TState transition exactly once
    -> commit accepted capability and conclude visit
```

Both observations already execute inside `PipelineRunContext.ExecuteAsync(...)`,
so the Tool's `LedgerUnitOfWork` keeps authored SQLite acceptance and automatic
journal persistence in one transaction.

Interaction request and response journal writes are awaited directly. Response
persistence moves before MAF delivery. Persistence failure prevents handler
dispatch for a request or state application for a response.

## Automatically Persisted Values

When the semantic step resolves persistent:

- accepted typed agent output payload;
- accepted typed capability request payload;
- typed interaction request payload;
- typed interaction response payload;
- declared standard failure evidence from `PipelineRunOutcome.Payload`; and
- all existing provenance, lifecycle, usage, action, and terminal metadata already
  recorded by the runtime journal.

Tandem does not automatically persist:

- `TState` snapshots;
- internal helper results;
- invalid, rejected, blocked, or corrected-away values;
- model reasoning, ordinary streamed prose, full prompts, or session history;
- arbitrary tool response bodies or source-file reads;
- raw command stdout/stderr beyond existing operational `events.jsonl`; or
- gate latches, routes, resume positions, and other runtime bookkeeping.

`events.jsonl` remains dashboard and debugging telemetry. Agents consume curated
accepted-value projections, not that file.

## Delivery Simplification

Delivery currently writes accepted values manually through
`IDeliveryRecordSink`. After automatic accepted-value journaling lands, simplify
it according to actual readers and authoritative ownership.

### Remove As Archival Duplication

- `CapabilityAcceptedRecord<TRequest>`.
- `IDeliveryRecordSink.AcceptCapabilityAsync(...)`.
- `delivery.capabilities.ask_planner`.
- `delivery.capabilities.submit_report`.
- `delivery.capabilities.write_checkpoint`.
- `PlannerDecisionRecord` persisted stream and
  `AcceptPlannerDecisionAsync(...)`.
- `ReviewDecisionRecord` persisted stream and the archival half of
  `AcceptReviewDecisionAsync(...)`.
- `AcceptedImplementationReportDocument` persisted document and
  `AcceptReportAsync(...)`.
- `HumanAnswerRecord` persisted stream and
  `TerminalHumanInteraction`'s manual save-before-completion path.
- Delivery terminal-outcome stream, which has no reader and duplicates the runs
  table plus runtime completion journal.

The Tool's `DeliveryLedger.ReadContextAsync(...)` reads the runtime journal once,
filters accepted records by kind plus stable step/capability/interaction identity,
and deserializes only the selected current Delivery payloads into Delivery types.
This is the role projection already owned by the Tool adapter, not new authoring
or persistence plumbing.

Simplify `DeliveryLedgerContext` to expose the semantic values it actually
formats, rather than one-field storage wrapper records. Join interaction request
and response by `RequestId` to reconstruct reviewer human-answer context.

### Keep Because It Is Earned

- Initial outcome baseline document: the packet is initial live state, not an
  accepted boundary. The existing initialized document durably preserves packet
  outcome IDs/descriptions without persisting all `TState`.
- Current outcome projection is derived on read by joining that baseline with the
  latest accepted `ReviewDecision`; stop rewriting it as a second copy of every
  review.
- `ProgressCheckpointRecord`: combines validated model input with authoritative
  Git changed files, packet outcomes, and accepted constraints before releasing a
  gate.
- `VerificationResultRecord`: verification is currently an Advanced operation,
  not an independent typed stage result, and its record contains authoritative
  command, stdout/stderr, timing, timeout, and candidate-integrity evidence.
- Publication candidate document and publication-result stream: they cross the
  SQLite/Git reconciliation boundary and are consumed by standalone `publish`.
- Runs table status and timestamps.

Delivery marks its composition `.Persist()` once. Samples remain ephemeral unless
they opt in and continue to avoid a transitive SQLite dependency.

## Inspection Journey

Add:

```text
tandem inspect <run-id>
```

The Tool already has `System.CommandLine`, the Tandem home resolver, SQLite store,
and `RunLedger.ReadAsync(...)`. Add an `inspect` command beside `run` and `publish`;
do not expose arbitrary SQL as the product journey.

Default output reads `runtime.journal` and presents one chronological timeline:

- run and step lifecycle;
- accepted typed values with pretty-printed payloads;
- interaction requests and responses;
- classified action attempts/results;
- command status and usage; and
- terminal status.

Operational tool arguments and streaming diagnostics remain in `events.jsonl`.
Join them only for `--tools` or an explicitly operational view, and label them as
telemetry rather than accepted facts.

Initial filters:

```text
--accepted
--step <id>
--type <name>
--tools
--json
```

`--json` returns a stable inspector DTO, not serialized SQLite row types.

## Implementation Sequence

### 1. Add Pipeline Persistence Policy

- Add pipeline default and participant overrides to `PipelineBuilder<TState>`.
- Add `.Persist()`, `.DoNotPersist()`, `.Persist(step)`, and
  `.DoNotPersist(step)`.
- Handle `PipelineInteraction<...>` as one semantic participant.
- Validate overrides during `Build(...)`.
- Carry immutable resolved policy into `Pipeline<TState>`, inspection, and
  `PipelineRunContext`.
- Add the narrow host persistence-observer SPI and preflight in `PipelineRunner`.

### 2. Preserve Accepted Payloads

- Structured output: pass existing payload and `OutputType` through
  `PipelineStructuredOutputAccepted`.
- Capabilities: pass existing payload, `typeof(TRequest)`, and accepted-call ID
  through `PipelineCapabilityAccepted`.
- Interactions: serialize typed request/response while creating their generic
  observations; move answered observation before MAF response delivery.
- Step completion: copy declared standard failure evidence into persistent journal
  records without duplicating accepted output/capability payloads or persisting
  state.
- Do not reserialize output/capability payloads in the Tool.

### 3. Enrich Journal Persistence

- Add optional `ValueType` and `Payload` to `RuntimeJournalRecord`.
- Add deterministic entry IDs for accepted semantic records.
- Make `LedgerPipelineObserver` implement the persistence SPI.
- Update Tool composite observer and preserve dashboard behavior.
- Prove old payload-less runtime records still deserialize.

### 4. Simplify Delivery

- Mark `DeliveryComposition.Build()` persistent.
- Remove duplicate capability, planner, reviewer, report, human-answer, and
  terminal archival writes listed above.
- Read accepted values from `runtime.journal` inside `DeliveryLedger` and build the
  existing bounded role context.
- Derive current outcomes from baseline packet outcomes plus latest accepted
  review.
- Keep checkpoint, verification, and publication persistence.
- Delete superseded record wrappers, stream/document declarations, callbacks, and
  tests.

### 5. Add Inspection

- Add `tandem inspect <run-id>` and filters.
- Render accepted payloads with provenance and ordering.
- Read JSONL only for requested operational detail.
- Update the Todo demo to use the inspector.

### 6. Documentation And API Proof

- Document persistence at composition root and participant opt-outs.
- Update `PipelineInspection` and exported API manifests.
- Add packed-consumer coverage for policy authoring without a Ledger package
  reference.
- Explain accepted SQLite history versus operational JSONL.

## Proof Requirements

### Policy

1. A pipeline without `.Persist()` runs without an observer or ledger as today.
2. A persistent pipeline fails before the start step when no persistence observer
   is supplied.
3. A persistent pipeline archives all inheriting accepted boundaries.
4. `.DoNotPersist(step)` preserves execution and metadata but omits semantic
   payloads for that participant.
5. `.Persist(step)` enables one participant in an otherwise ephemeral pipeline.
6. Unknown or unregistered participant overrides fail at build time.
7. One built pipeline executes concurrent run IDs without sharing policy or data.

### Outputs

8. Accepted output stores exact existing serializer payload, descriptive type,
   accepted ID, step, and timestamp.
9. Invalid, rejected, and corrected-away candidates are absent.
10. A corrected response produces exactly one accepted payload.
11. Journal failure prevents output state mapping and routing.

### Capabilities

12. Accepted capability stores the already serialized validated request,
    accepted-call ID, capability identity, step, and timestamp.
13. Invalid, blocked, failed, or conflicting calls produce no accepted payload.
14. Retry of one accepted-call identity converges on one journal entry.
15. Journal failure prevents state mapping, gate release, and visit acceptance.

### Interactions And Outcomes

16. Interaction request is durable before host handler dispatch.
17. Interaction response is durable before MAF response delivery and state apply.
18. Request/response retries converge by deterministic request-based IDs.
19. A participant opt-out omits both interaction payloads while preserving live
    behavior.
20. Declared standard failure evidence persists without duplicating accepted
    output or capability payloads.
21. `TState`, successful state-return payloads, reasoning, prompts, and arbitrary
    tool bodies remain absent.

### Delivery

22. Todo Delivery automatically records planner output, `ask_planner`, report,
    reviewer output, and any human exchange without archival callbacks.
23. Executor, planner, and reviewer receive the same bounded semantic context after
    session discard or rotation.
24. Current outcomes derive correctly from packet baseline plus latest review.
25. Checkpoint enrichment, verification evidence, publication reconciliation, and
    run status remain durable and authoritative.
26. Removed streams/documents have no remaining readers or writers.

### Inspection

27. `tandem inspect <run-id>` shows accepted values chronologically after process
    exit.
28. Filters select accepted values, step, type, tools, and JSON output correctly.
29. Operational events are visibly distinct from accepted facts.
30. Inspector output does not require original CLR types to be loadable.

### Repository

31. SQLite ordering, contention, idempotency, contract, and reopen tests remain
    green.
32. Core and packed consumers acquire no transitive SQLite dependency.
33. Public authoring APIs expose no SQLite, MAF, or storage JSON types.
34. `task check`, `git diff --check`, and Meridian validation pass.

## Non-Goals

- Persisting every CLR return value.
- Adding independent typed stage results in this campaign.
- Persisting `TState` or reconstructing a dead workflow.
- Workflow resume or Durable Task replacement.
- Event sourcing application state.
- Persisting model reasoning, full prompts, streaming prose, or arbitrary tool
  bodies by default.
- Persisting raw command output automatically.
- Automatically inventing Delivery projections in Core.
- Requiring application values to implement persistence contracts.
- Requiring output/capability save callbacks.
- Adding another ledger, storage provider, registry, or serializer.
- Making SQLite transitive to minimal consumers.
- Migrating or rewriting existing run rows; additive runtime records remain
  readable and new payloads begin with new runs.

## Completion Gate

- `.Persist()` automatically archives accepted typed values through the existing
  SQLite journal path.
- Participant-level overrides work without changing participant definitions.
- Application values require no persistence mapping or marker interfaces.
- Persistence failure prevents semantic progress at acceptance boundaries.
- Delivery no longer copies accepted values solely for archival storage.
- Delivery retains only packet baseline, authoritative enrichment, verification,
  publication reconciliation, and run status persistence.
- `tandem inspect <run-id>` provides the promised after-the-fact story.
- JSONL remains operational telemetry, not semantic truth or agent memory.
- No runtime bookkeeping enters `TState`.
- All repository and Meridian checks pass.
