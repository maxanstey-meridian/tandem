# Durable Ledger And Composable Gates Plan

## Objective

Add durable product memory and composable agent gates without preserving,
replacing, or emulating Durable Task.

The runtime and ledger are independent:

- MAF runs the live workflow in one process.
- `TState` carries live routing state in that process.
- The ledger records accepted product facts for later inspection and agent
  judgment.
- Gates control what an agent may do during the live run.

Process exit kills the workflow. Nothing in this plan can resume it.

## Design Status

This plan is ready for characterization and contract work, not implementation.
The following decisions must be made before checkpoint gate mechanics or durable
record payloads are finalized:

- whether semantic checkpointing is a product invariant that must block further
  mutation, or only a context-window management mechanism that should use MAF's
  maintained compaction support;
- the retention, deletion, and redaction policy for human answers, verification
  output, repository paths, and model-authored records; and
- whether the local ledger is retained indefinitely or receives an explicit
  operator deletion surface.

Until the first decision is made, characterize MAF compaction and
`AIContextProvider`. Do not build a custom checkpoint gate merely to solve token
pressure that the maintained framework already handles.

## Clean Boundary

This plan does not block, sequence, or modify the Durable Task removal. The
in-process runtime work should delete Durable Task, restart support, attach,
rehydration, and their persisted machinery without waiting for this plan.

This plan:

- migrates no existing run;
- reads no Durable Task state;
- preserves no compatibility format;
- provides no temporary dual-write path;
- reconstructs no `TState`;
- persists no routing position, pending request, MAF event, session continuation,
  or gate latch; and
- starts recording facts only for runs created after the ledger exists.

Existing files survive only when their current design still needs them. They are
not retained to seed or support the ledger.

## Responsibilities

### MAF

- Execute the authored workflow.
- Route typed state and outcomes.
- Handle live request/response.

### `TState`

- Carry current composition state.
- Support predicates and routing.
- Exist only for the live process.

### Tandem Ledger Infrastructure

- Bind records to a run.
- Append ordered records idempotently.
- Store current typed documents.
- Read records and documents.
- Persist through a maintained SQLite provider.
- Bind a ledger capability to the current run at execution time without storing
  a run ID in `TState` or ambient singleton state.
- Arbitrate ordering, idempotency, and optimistic concurrency through SQLite
  constraints and atomic statements.

### Composition

- Define the facts meaningful to its agents.
- Decide when a fact is accepted.
- Build bounded context from recorded facts.
- Define state guards, latched triggers, blocked effects, warnings, remediation,
  and release bindings.

Delivery owns planner decisions, progress checkpoints, outcomes, reports,
verification, reviews, and human exchanges. These do not become universal Tandem
domain concepts.

The run host and standalone commands are separate processes. Semantic agent
capabilities execute locally in the run host through MAF function middleware.
Every process that needs durable product facts shares one local database at
`$TANDEM_HOME/ledger.sqlite3`. Connections are opened per operation or unit of
work; no process owns an ambient current run.

## Minimal Ledger

The generic substrate needs only three storage concepts:

```text
runs           run identity, composition, status, timestamps
run_entries    ordered append-only records
run_documents  current replaceable records
```

Operational dashboard events may remain in their existing implementation. Do
not move them into SQLite merely to unify storage.

Conceptual fields:

```text
runs
    run_id
    composition
    status
    started_at
    updated_at
    ended_at

run_entries
    run_id
    stream
    sequence
    entry_id
    payload
    recorded_at

run_documents
    run_id
    key
    version
    payload
    updated_at
```

Required behavior:

- appending the same entry ID twice returns the original accepted entry;
- conflicting content under the same entry ID fails;
- stream reads are ordered;
- document writes support an expected version;
- runs are isolated; and
- closing and reopening the store preserves records.

Required relational constraints:

```text
runs
    primary key (run_id)
    check composition is not blank
    check status is a known value

run_entries
    primary key (run_id, stream, sequence)
    unique (run_id, entry_id)
    foreign key (run_id) references runs(run_id)
    check stream and entry_id are not blank
    check sequence >= 1

run_documents
    primary key (run_id, key)
    foreign key (run_id) references runs(run_id)
    check key is not blank
    check version >= 1
```

Entry identity is unique across a run, not merely within one stream. Reusing an
entry ID with another stream or payload is a conflict. Idempotency comparison
uses deterministic serialized bytes or a stored content hash. A replay returns
the original sequence and timestamp.

Sequence allocation and insert happen atomically. A single SQLite write
transaction may use `max(sequence) + 1` under `BEGIN IMMEDIATE`; do not add a
separate stream-head table unless measurement proves it necessary.

Document version `0` means "must not exist." A successful create returns version
`1`; a positive expected version performs one atomic compare-and-swap and
increments by exactly one. Reads return both the typed value and its version.
There is no blind document overwrite initially.

The adapter enables foreign keys on every connection, initializes WAL mode,
uses a bounded busy timeout and bounded retry for SQLite lock contention, and
documents that the database must be on a local filesystem. Successful semantic
acceptance uses a durability setting appropriate for surviving ordinary process
or OS failure.

"No migration framework" does not mean "no schema identity." Initial creation is
idempotent, the schema has an explicit version such as `PRAGMA user_version`, and
an unknown version fails clearly rather than being guessed or rewritten.

No query-oriented secondary indexes, arbitrary SQL API, remote storage,
replication, event sourcing, or migration framework is needed initially.
Indexes required by primary and unique constraints are part of correctness, not
query optimization. Retention remains an explicit unresolved product decision.

## Typed Boundary

Composition code must use typed records, not raw JSON:

```csharp
public sealed record LedgerStream<TEntry>(string Name);

public sealed record LedgerDocument<TDocument>(string Name);
```

The run-bound capability needs the equivalent of:

```text
AppendAsync(stream, entryId, entry)
ReadAsync(stream)
ReadDocumentAsync(document)
WriteDocumentAsync(document, value, expectedVersion)
```

Append returns a typed accepted-entry envelope containing sequence and recorded
time. Document reads and writes return a typed envelope containing value and
version. The exact replay contract for a document write must be settled before a
ledger-backed capability updates a current document; an invocation replay must
not advance a document version twice.

The exact API should follow the landed Tandem authoring style. Preserve these
constraints:

- JSON and SQLite stay in Infrastructure;
- authors do not manually carry an internal run ID through `TState`;
- no public `object` or dictionary-based record API is introduced; and
- adding a composition record type does not require a new database table.

Stream and document names are persisted contract identities. Registration fails
if two record types claim the same name. Breaking payload changes use a new
contract name or an explicit payload version; silently deserializing an old name
as a new incompatible type is forbidden.

Pipeline definitions are reusable and may execute concurrent runs. A singleton
composition must never capture a mutable "current run" ledger. Infrastructure
may select a run using `PipelineRuntime.RunId`, the local
`AgentCapabilityContext` accepted-call identity, or the host's run identity,
then hand composition code a run-bound typed capability.
Run identity is explicit at the infrastructure boundary and absent from record
method calls and `TState`.

## Delivery Records

Start with facts Delivery already uses.

### Append-Only

- planner decisions, rationale, constraints, and evidence;
- progress checkpoints;
- human questions and answers;
- verification results;
- reviewer decisions and findings; and
- publication results.

### Current Documents

- outcome progress and evidence; and
- the accepted implementation report; and
- the current publication candidate: repository, workspace, packet identity,
  pinned base SHA, and candidate SHA.

A progress checkpoint is agent memory, not a workflow checkpoint. It contains:

- summary;
- current outcomes;
- changed and inspected files;
- accepted constraints;
- uncertainties; and
- next action.

Derive facts mechanically when Tandem already knows them. The model should not
invent changed files, run identity, or current outcome status.

The outcome document has one named owner and state transition. Executor claims
do not mark outcomes delivered. Checkpoint records combine validated model input
with mechanically derived changed files, accepted constraints, and current
outcome state in the parent run process.

Role projections have deterministic limits: selected current documents, a fixed
number of recent records per stream, a character or token budget, stable ordering,
and an explicit truncation marker. Persisted model and human text remains
untrusted content and is delimited as data in prompts.

## Acceptance Boundaries

Record a fact where it becomes accepted:

- persist a validated planner decision before routing on it;
- persist a human answer before sending the live workflow response;
- persist a progress checkpoint before resetting the executor conversation;
- persist verification when each check completes;
- persist a validated review before routing on it;
- persist the report as the acceptance of a validated `submit_report` request;
- update run status at an authored terminal node, on an orderly host fault, or on
  orderly cancellation; and
- persist publication only after its Git side effect has been reconciled.

The order is:

```text
validate
    -> persist accepted fact
    -> update live TState
    -> continue routing or release gate
```

If persistence fails, the current operation fails. If the process dies after the
record is written, the record remains and the workflow stays dead.

Local capabilities receive a Tandem-owned accepted-call identity containing run,
block, invocation, and capability IDs after request validation and before state
transition. A ledger-backed asynchronous acceptance callback:

1. Derives authoritative host-owned fields.
2. Commits the semantic ledger operation idempotently using the accepted-call identity.
3. Returns the updated `TState` only after the durable commit succeeds.
4. Allows the invocation-local acceptance slot to commit.
5. Allows routing, gate release, and session policy to continue.

If durable acceptance fails, the capability returns no accepted result, performs
no state transition, and releases its provisional invocation slot so a corrected
call can try in the same MAF session. Retrying the same accepted-call identity
must converge on the original durable fact; it does not replay or resume a dead
workflow.

Structured planner and reviewer decisions need an asynchronous acceptance seam
after final validation and before their updated state is emitted. Human answers
are persisted under `(run_id, request_id)` before the broker receives the answer.
Verification is persisted after command and candidate-integrity checks but before
the result enters `TState`. Generic block-completion observation remains
operational telemetry and is not a semantic acceptance boundary.

Run status transitions are compare-and-swap and idempotent:

```text
Running -> Ready | Failed | Faulted | Cancelled
```

Repeating the same terminal transition succeeds; changing one terminal status to
another conflicts. `WaitingForHuman` remains live dashboard state because pending
requests are not durable. A hard-killed process remains `Running`, meaning "no
terminal outcome was recorded," not "the process is currently alive."

Terminal status writes use a fresh bounded cancellation token so cancellation of
the workflow does not suppress orderly status persistence.

### Publication Reconciliation

Publication crosses from SQLite into Git and cannot be made one transaction.
It therefore has an explicit reconciliation contract:

- derive a deterministic operation identity from run, repository, branch, and
  candidate SHA;
- perform every possible precondition check before `git push`;
- treat an existing branch at the exact candidate SHA as an already-applied
  success;
- treat an existing branch at another SHA as a conflict;
- append the publication result idempotently after reconciling the branch; and
- never report "not published" when the target branch exists at the requested
  candidate.

A `Ready` run remains `Ready`; publication is a separate fact rather than a run
status. The Delivery-owned publication-candidate document is the authoritative
input for standalone `publish`.

## Agent Consumption

The ledger matters only if agents use it.

Inject a bounded projection before relevant invocations:

- executor: current outcomes, latest checkpoint, accepted constraints, and
  recent verification failures;
- planner: previous decisions, current outcomes, and unresolved constraints;
- reviewer: outcomes, report, verification, constraints, and human answers.

First characterize MAF's maintained `AIContextProvider` as the injection seam.
Use Tandem message augmentation only if the framework seam cannot preserve the
required role ownership, ordering, or testability.

Add narrow read capabilities only when an agent needs deeper history:

- `get_decisions`;
- `get_outcomes`;
- `get_latest_checkpoint`; and
- `get_reviews`.

These return Delivery-owned projections. Do not expose SQL, storage keys, raw
JSON, or unrestricted cross-run reads.

## Composable Gates

The current mutation interceptor and checkpoint branch in `AgentBlock` are
specialized, and checkpoint-only prompting does not hard block mutation. Converge
retained policies on one small enforcement mechanism.

There are two gate forms.

A state guard defines:

- stable identity;
- an incoming-state predicate;
- blocked tool effects;
- blocked-action message; and
- remediation capability or instruction.

Planner mutation authorization is a state guard. It remains active while
`MutationAuthorized` is false. `ask_planner` routes to remediation; only an
accepted planner decision can change the state that opens the guard.

A latched gate defines:

- stable identity;
- after-turn trigger;
- blocked tool effects;
- blocked-action message;
- capability that releases it; and
- session action after release.

The first latched trigger point is after an outer agent request, when its latest
usage is known.

Conceptually:

```csharp
agent.WithGate(
    AgentLatchedGate.Create(
        id: "checkpoint-required",
        trigger: turn => turn.Usage.CurrentContextTokens >= 50_000,
        blocks: ToolEffect.WorkspaceMutation,
        message: "Context limit approaching. Call write_checkpoint before further mutation.",
        release: writeCheckpoint
    )
);
```

The fluent shape is provisional. Keep the implementation local to agent
execution; do not create a general policy engine. State guards and latched gates
share enforcement and effect classification, not one false release model.

Gate latches are immutable values in `PipelineRuntime`, keyed by block and gate.
They are not ledger records and are not restored after process exit. They survive
workflow routing within a live run and remain isolated when one built pipeline
executes concurrent run IDs. Optional open/close observations may be sent to the
existing dashboard event path.

Do not put latches in mutable `AgentBlock` fields or a singleton current-run
registry.

## Tool Effects

Introduce only the classifications needed for current enforcement:

```text
Read
WorkspaceMutation
LifecycleTransition
```

- reads and searches are `Read`;
- writes, moves, deletes, and workspace-changing Git operations are
  `WorkspaceMutation`; and
- agent capabilities are `LifecycleTransition`.

Classify SDK tools once at their adapter or registration boundary. Do not repeat
tool-name prefix matching in every composition policy. A custom tool used by a
mutation-enabled agent must declare its effect.

For MAF 1.16, characterize classification against the actual exposed file tools:

```text
Read
    file_access_read
    file_access_ls
    file_access_grep

WorkspaceMutation
    file_access_write
    file_access_delete
    file_access_replace
    file_access_replace_lines
```

Use maintained framework constants where available. Fail agent construction if
an exposed invocable custom tool has no effect classification. An unknown tool
cannot be proven non-mutating at invocation time, so runtime name guessing is not
a completeness mechanism. Detect name collisions as configuration errors.

Lifecycle capabilities declare `LifecycleTransition` at registration. Do not
blanket-exempt lifecycle tools from gate evaluation. A latched gate always allows
its exact releasing capability; other capabilities are evaluated normally.

Use MAF's maintained function-invocation middleware (`AsBuilder().Use(...)`) to
compose effect lookup, gate enforcement, tool outcome collection, and lifecycle
termination around the framework continuation. Do not replace
`FunctionInvokingChatClient.FunctionInvoker`, which assumes all invocation
handling.

## Gate Journey

For each Tandem agent request:

1. Execute under the gates active at turn start.
2. Collect current usage and tool outcomes.
3. Evaluate after-turn triggers.
4. Latch newly activated gates before another turn begins.
5. Block tools whose effects match an active gate.
6. Return the gate's actionable warning for blocked attempts.
7. Direct a state guard to remediation or a latched gate to its release
   capability.
8. Validate any capability request.
9. Persist its semantic record when it has one.
10. Apply the live state transition.
11. For a latched gate, clear only the gate released by that capability.
12. Apply the configured session reset when the latched gate requests one.

A Tandem gate turn is one outer `agent.RunStreamingAsync` request, including the
MAF function-invocation loop it owns. All tools requested within that outer call
execute under its starting gate snapshot. Usage observed when it completes can
gate a correction/continuation request or a later workflow invocation, but cannot
retroactively block tools from the completed response. If same-response blocking
becomes a requirement, it needs a lower chat-response hook and is outside this
after-turn design.

Evaluate triggers after every outer request, before starting structured-output
correction or continuation. `CurrentContextTokens` is the latest completed model
request's input plus output count. Track cumulative token spend separately; do
not sum retained context across requests and call it current occupancy.

A threshold discovered at turn end cannot undo mutations from that completed
turn. It controls subsequent calls and turns.

Planner authorization and checkpoint pressure are independent gates. Releasing
one must not release the other.

One release binding releases one gate ID. Session reset is an idempotent output
transition after semantic acceptance, not an effect performed in middleware.
Retrying an already committed accepted-call identity derives the same semantic
fact without emitting a second release/reset observation.

## Checkpoint Journey

This journey applies only if semantic checkpointing is confirmed as a product
invariant rather than replaced by MAF compaction for context management.

```text
executor turn completes above threshold
    -> checkpoint gate closes
    -> workspace mutation is blocked
    -> write_checkpoint is requested
    -> request is validated
    -> parent enriches it from Git and accepted ledger state
    -> authoritative progress checkpoint is appended idempotently
    -> live executor state records checkpoint completion
    -> checkpoint gate opens
    -> executor session and current-context usage reset
    -> next executor prompt uses a bounded ledger projection
```

The new checkpoint ledger replaces latest-only checkpoint history. Do not add a
reader for old `CheckpointPayload` values or removed lifecycle receipt files.

## Implementation Sequence

### 1. Characterize Current Gates

Prove the behavior that matters:

- planner authorization blocks workspace mutation;
- usage is measured;
- threshold detection requests checkpointing;
- accepted checkpointing resets executor context; and
- checkpoint routing returns to the executor.

Also prove the current defect: checkpoint-only prompting does not hard block an
already-authorized mutation. This test should turn green under the new gate.

Characterize against real MAF 1.16 behavior rather than hand-written tool lists
or simulated checkpoint blocks:

- exact Harness file-tool inventory;
- function middleware ordering and lifecycle termination;
- usage emitted across a multi-iteration function-calling request;
- whether `AIContextProvider` preserves the required role-specific projection;
- built-in compaction behavior and session continuity; and
- outer-request versus inner-model-call timing.

Resolve the checkpoint product-invariant decision after this characterization.

### 2. Add The SQLite Adapter

- Add typed stream and document contracts.
- Add one SQLite adapter.
- Add explicit schema bootstrap/version checks, run creation, and terminal
  status transitions.
- Use primary/unique/foreign-key/check constraints and atomic writes as the
  correctness mechanism.
- Test ordering, idempotency, version conflicts, rollback, reopen, lock timeout,
  and run isolation against a real SQLite database.
- Add separate-process contention tests because the run host and standalone
  publisher do not share process-local locks.

### 3. Add Semantic Acceptance Seams

- Add an asynchronous post-validation/pre-state seam for structured decisions.
- Commit ledger-backed local capability acceptance before state transition.
- Persist human answers before broker delivery.
- Persist authoritative verification before it enters `TState`.
- Keep operational event observers out of semantic acceptance.
- Prove persistence failure prevents routing, release, and state update.

### 4. Add Delivery Records

- Define Delivery-owned record types.
- Initialize outcome progress from the packet.
- Record new planner decisions, checkpoints, human exchanges, verification,
  reviews, reports, and terminal outcomes.
- Record a current publication candidate whenever candidate capture succeeds.
- Do not import any existing persisted artifact.

### 5. Add Ledger Context

- Build bounded role-specific projections.
- Add only the read capabilities needed by current prompts.
- Prefer MAF `AIContextProvider` if characterization confirms it is the honest
  context seam.
- If semantic checkpointing remains, reset the executor after checkpoint
  acceptance and reconstruct its prompt from ledger records.

### 6. Add Gate Mechanics

- Add state guards and latched gates as separate activation models.
- Include latest-request context usage in after-turn observations and track
  cumulative spend separately.
- Add run-isolated gate latches to `PipelineRuntime`.
- Add minimal tool-effect classification.
- Fail construction for exposed unclassified invocable tools.
- Enforce active gates through MAF function middleware before tool invocation.
- Release a latched gate only after its capability is semantically accepted.

### 7. Migrate Delivery Policies

- Express planner mutation authorization as a state guard.
- If retained, express checkpoint pressure as an after-turn latched gate.
- Persist an authoritative checkpoint before release.
- Remove the old checkpoint-only branch and duplicated mutation prefix checks.

### 8. Make Publication Reconciliable

- Move publication lookup and validation behind a Delivery-owned application
  capability using the publication-candidate document.
- Treat an existing exact branch as successful reconciliation.
- Append one idempotent publication result after the Git ref is known.
- Prove recovery from process failure immediately after `git push`.

### 9. Delete Superseded Code

- Remove current fields and files only when the live design no longer consumes
  them.
- Make SQLite the source of truth for new-run identity/status and publication
  input; retire `run.json` rather than maintaining two semantic sources.
- Keep `events.jsonl` as dashboard telemetry; do not restore lifecycle receipts
  or another capability transport artifact.
- Do not add migration readers or dual writers.
- Let the separate runtime plan delete all Durable Task machinery independently.

## Acceptance Tests

### Ledger

1. Entries remain ordered after reopening SQLite.
2. Repeating an entry ID does not duplicate it.
3. Repeating an entry ID returns its original sequence and timestamp.
4. Conflicting stream or content under one entry ID fails.
5. Two separate processes append different IDs to one stream with unique,
   contiguous ordering.
6. Two separate processes append the same ID and both observe one accepted row.
7. Primary, unique, foreign-key, non-empty, sequence, version, and status
   constraints are enforced.
8. Unknown schema versions fail clearly.
9. A bounded lock timeout fails rather than waiting forever.
10. Document create, update, stale update, and replay semantics are explicit.
11. Runs remain isolated.
12. Typed records round-trip without exposing JSON.
13. Registering two record types under one persisted name fails.

### Delivery

14. A planner decision is recorded before mutation authority opens.
15. Structured-decision persistence failure prevents state update and routing.
16. Multiple decisions remain queryable in order.
17. Human answers exist before broker delivery; a conflicting answer for the
    same request ID fails.
18. Verification persistence failure prevents the result entering `TState`.
19. Checkpoints append history rather than replacing it.
20. Checkpoint outcomes and changed files come from authoritative sources.
21. Retrying one accepted-call identity converges on one semantic commit before
    state transition.
22. Human, verification, review, report, and terminal records survive process
    exit.
23. New agents receive deterministically bounded role-specific context with a
    visible truncation marker.
24. The publication-candidate document matches the candidate accepted at the
    Ready terminal.

### Gates

25. Construction fails when a gated agent exposes an unclassified invocable
    tool.
26. The classification proof covers the actual MAF file-tool collection.
27. Crossing the threshold closes the checkpoint gate before another outer
    agent request.
28. Workspace mutation attempted while gated is not executed.
29. Read tools remain available when the policy allows them.
30. Invalid checkpoint calls do not release the gate.
31. The checkpoint record exists before release.
32. Release resets executor session and latest-context usage exactly once.
33. The next executor invocation creates a fresh session and receives bounded
    ledger context.
34. Opening the planner state guard does not release the checkpoint latch, and
    releasing the checkpoint latch does not grant planner authorization.
35. A checkpoint latch survives executor-to-planner routing.
36. Concurrent runs through one built pipeline do not share latches.
37. Duplicate accepted-call handling does not emit another release/reset observation.
38. Multiple tool calls in one model response all use the gate snapshot active
    at that outer request's start.

### Publication

39. Failure immediately after `git push` is reconciled on retry.
40. An existing branch at the requested candidate converges to one publication
    record.
41. An existing branch at another candidate conflicts.
42. Concurrent publication of the same branch and candidate converges without
    duplicate records.

### Runtime Boundary

43. Process exit kills the workflow.
44. Ledger records remain readable.
45. No API can resume the dead workflow from those records.
46. Orderly authored failure, host fault, and cancellation record distinct
    terminal statuses.
47. A hard-killed run remains last-known `Running` and is never presented as
    proof of liveness.

## Non-Goals

- Durable Task replacement or compatibility.
- Workflow restart, resume, or reconstruction.
- Persisted MAF state or checkpoints.
- Persisted `TState` snapshots.
- Pending-request recovery.
- Gate recovery.
- Importing old runs, receipts, events, projections, or checkpoint payloads.
- Dual writes or transition shims.
- Daemons, queues, leases, campaigns, convergence, or acceptance machinery.
- Event sourcing.
- Remote storage.
- A universal decision, checkpoint, review, or outcome model.
- A general rules engine.
- Treating a `Running` ledger row as a process-liveness guarantee.
- Making SQLite and Git one fictional transaction.

## Completion Gate

- The SQLite adapter passes real-engine integration tests.
- SQLite correctness also passes separate-process contention and lock-timeout
  tests.
- Delivery records new accepted facts in typed streams and documents.
- Agents consume bounded ledger context.
- The retention/redaction decision is recorded before sensitive durable payloads
  are introduced.
- If semantic checkpointing remains, its history survives process exit without
  enabling resume.
- If semantic checkpointing does not remain, MAF compaction owns context-window
  pressure and no replacement checkpoint gate is built.
- Gates evaluate latest-request usage and enforce declared tool effects through
  maintained MAF function middleware.
- Ledger-backed capabilities persist before releasing their gate.
- Planner and checkpoint gates compose independently.
- Publication reconciles an already-created exact Git branch and records one
  idempotent result.
- SQLite, not `run.json`, is the semantic source of truth for new-run status and
  publication input.
- No old persisted format is read or dual-written.
- No runtime cutover or Durable Task deletion depends on this plan.
- Relevant tests, `git diff --check`, and Meridian validation pass.
