# In-Process Runtime Plan

## Objective

Replace Tandem's Durable Task runtime with one process-lifetime runtime built on
Microsoft Agent Framework's maintained in-process workflow execution.

Each `tandem run` process owns exactly the live runs it starts, including their
workflow objects, state, agent sessions, pending human requests, and answers.
There is no shared daemon, central panel, scheduler, task hub, workflow database,
or cross-process continuation.

The product contract is intentionally simple:

- a run lives for the lifetime of its `tandem run` process;
- a human wait consumes no blocked worker thread;
- other independent `tandem run` processes continue normally;
- exiting, crashing, deploying, or rebooting destroys active runs; and
- Tandem does not resume an interrupted run after restart.

## Implementation Status

Implemented on 2026-08-07:

- `InProcessPipelineRunner` is the only production workflow runner;
- typed MAF requests are adapted through Tandem-owned serializable values;
- `InMemoryExternalRequestBroker` owns run/request identity, one-answer
  acceptance, cancellation, and cleanup;
- `tandem run` owns its workflow and human input for the process lifetime;
- `attach`, Durable Task packages, scheduler configuration, durable projection
  fields, and scheduler-only tests are removed;
- serialized Harness sessions now travel in live `PipelineRuntime`; session and
  profile files are removed;
- standalone `publish` and its required final projection remain; and
- real MAF in-process tests cover output, declared failure, faults, cancellation,
  typed continuation, request isolation, broker cleanup, and a waiting run not
  blocking an independent run.

## Sequencing

This work begins only after the active agent/capability refactor lands and the
repository gate passes. It must not be developed concurrently against moving
authoring contracts.

After that refactor lands:

1. Inspect the resulting runtime and host boundaries.
2. Re-map Durable Task dependencies from the current implementation.
3. Confirm the state-first authoring contract remains unchanged.
4. Implement this plan against the landed code.

## Provisional Inventory

This inventory was taken while the agent/capability refactor was still active.
Refresh it after that refactor lands before editing shared runtime or host files.

### Existing In-Process Foundation

- `Pipeline` already contains an ordinary MAF `Workflow`, exposed internally by
  `PipelineMafBridge`.
- Composition and infrastructure tests already execute real workflows through
  `InProcessExecution.RunStreamingAsync` and `WatchStreamAsync`.
- `SupportCompositionTests` already proves typed request/response continuation
  through a real `RequestInfoEvent`, `ExternalRequest.CreateResponse`, and
  `StreamingRun.SendResponseAsync`.
- The targeted Support in-process interaction test currently passes both cases.
- `Infrastructure/WorkflowRunner.cs` is an unused older in-process runner for a
  single implementation block. Replace or remove it rather than introducing a
  second competing runner.

### Current Durable Task Surface

Production Durable Task orchestration is concentrated in:

- `Tandem.Tool/Program.cs`: scheduler host setup, durable workflow client,
  completion waiting, attach, and external-event answers;
- `Tandem.csproj`: Durable Task workflow, client, and worker package references;
  and
- the test project and `tests/Tandem.Tests/Durable`: scheduler fixtures,
  restart/rehydration proofs, durable request probes, and mixed framework-fit
  characterization.

Do not delete the entire durable test directory mechanically. Retain or rewrite
tests that prove runtime-independent behavior such as ordered routing, loops,
events, session continuity, tools, and human continuation. Delete scheduler,
restart, rehydration, and cross-process continuation assertions.

### Interaction Authoring Has Already Moved

The active refactor has already introduced the semantic public interaction:

```csharp
PipelineNodes.WaitFor<TState, TRequest, TResponse>(...)
```

`PipelineInteraction<TState, TRequest, TResponse>` privately expands to request,
MAF `RequestPort`, and resume nodes. Composition routes to and from one semantic
interaction. The public authoring cleanup originally described in step 8 is
therefore already complete provisionally; refresh this conclusion after the
active refactor lands.

The internal request stage currently saves the complete execution message
through MAF workflow state and the resume stage restores it. This state is still
required by the current internal topology during the first in-process cut. Do
not classify it as removable durability noise until a proven simpler internal
interaction can preserve the same envelope and typed response semantics.

### MAF Streaming Semantics

The pinned MAF SDK documents and existing tests establish that:

- `WatchStreamAsync` pauses at a pending request by default and resumes after
  `SendResponseAsync`;
- the pending wait is asynchronous and occupies no worker thread;
- MAF supplies request identity and validates response types through the
  originating `ExternalRequest`;
- cancelling stream enumeration does not cancel the workflow run; the adapter
  must call `CancelRunAsync`; and
- disposing the `StreamingRun` is part of every completion, fault, and
  cancellation path.

### Persistence Classification

Remove persistence whose only purpose is Durable Task restart or cross-process
continuation. Initially retain persistence with a demonstrated live product or
process-boundary purpose:

- `EventStore` feeds the current live dashboard;
- `RunProjectionStore` supports final candidate publication, including the
  standalone `publish` command;
- agent session files currently support conversation continuity within a live
  run; and
- lifecycle receipts currently communicate accepted capability outcomes across
  the existing MCP stdio subprocess boundary and suppress duplicate calls
  within a live operation.

Those retained mechanisms still require a later purpose-by-purpose audit. They
must not be kept merely because they exist, but they also must not be deleted as
workflow durability noise while they serve current live execution.

### Refreshed Implementation Map

This map reflects the working tree on 2026-08-07 while the agent/capability
refactor was still in progress. The active refactor also modifies
`Tandem.Tool/Program.cs`, the interaction authoring files, Delivery composition,
and their tests. Do not edit those shared files until that work is handed off and
the repository gate passes.

#### Production Execution Path

The only production Durable Task host is `Tandem.Tool/Program.cs`:

- `RunAsync` builds the Delivery `Pipeline`, extracts its MAF `Workflow`, starts a
  Generic Host with `ConfigureDurableWorkflows`, invokes `IWorkflowClient.RunAsync`,
  and waits through `IAwaitableWorkflowRun.WaitForCompletionAsync`;
- the foreground `DashboardLoop` tails `events.jsonl`, while a background task
  waits for durable completion and writes terminal projection state;
- dashboard answers call `DurableTaskClient.RaiseEventAsync` with the fixed
  `HumanInput` event name;
- `AttachAsync` rebuilds and registers the workflow, reconnects to the durable
  instance, and can answer or publish it; and
- `PublishAsync` is independent of the live workflow and consumes persisted
  `run.json` plus the candidate workspace.

No other production source references Durable Task. `Tandem.Tool.csproj` receives
the packages transitively from `Tandem.csproj`; the direct production package
references are currently all in `Tandem.csproj`.

`Infrastructure/WorkflowRunner.cs` has no consumers. It builds and runs one old
`ImplementationBlockExecutor` workflow and returns `BlockResult`; it is not a
pipeline runner and should be replaced rather than generalized alongside a new
runner.

#### Current MAF Boundary

- `Pipeline` owns an internal MAF `Workflow`.
- `PipelineMafBridge.GetWorkflow` is the existing internal extraction point.
- MAF in-process execution is repeated in test-local helpers such as
  `CompositionRunner`, Songwriter, Debate, generated-authoring, envelope, and
  lifecycle tests.
- Those helpers all implement the same essential loop: create a `StreamingRun`,
  inspect `WorkflowErrorEvent` and `ExecutorFailedEvent`, capture a typed
  `WorkflowOutputEvent`, and dispose the run.
- `ExternalRequest.RequestId` supplies the unique request identity.
- `ExternalRequest.CreateResponse` enforces the originating response type.
- `StreamingRun.WatchStreamAsync` does not cancel workflow execution when stream
  enumeration is cancelled; `CancelRunAsync` has no cancellation-token overload
  in the pinned SDK.

The first runner tests should replace these duplicated test loops with the real
Tandem adapter. MAF-only characterization tests may continue to call MAF directly
when MAF itself is the subject.

#### Interaction And Terminal Path

`PipelineInteraction<TState, TRequest, TResponse>` is already the semantic public
operation. Its internal topology is:

```text
request stage -> MAF RequestPort -> resume stage
```

The request stage saves the complete `PipelineMessage<TState>` in MAF workflow
state under the interaction scope and the run ID. The resume stage reads exactly
one saved message, applies the typed response, and emits `request.resumed`.
Retain this topology for the first runtime cut.

The current Delivery terminal path is split:

- `RunEventBlockExecutionObserver` sees the request stage output
  (`HumanQuestion`) and appends `human.requested`;
- `DashboardReducer` turns that projected event into its in-memory
  `PendingHumanRequest` view;
- `DashboardLoop` captures an untyped answer string and invokes a callback; and
- Durable Task currently correlates that answer by run ID and the fixed event
  name, not by MAF `ExternalRequest.RequestId`.

The in-process cut must join the dashboard view to the live MAF request identity.
The runner or its internal request adapter must register the original
`ExternalRequest` before an answer can be accepted, and the terminal callback
must resolve that exact pending request. Do not make the event projection or
dashboard model the authority for pending-request lifecycle.

There are currently three related value shapes:

- `HumanQuestion` and `HumanAnswer` in `Domain/HumanInput.cs` are the authored
  Delivery interaction values;
- `HumanRequestView` is dashboard presentation state derived from events; and
- `RunProjection.PendingHumanRequest` is persisted projection metadata.

`RunProjection.PendingHumanRequest` is never populated by production code; only
null values are written. It can be removed with attach/restart metadata. The
dashboard view remains useful, but it must not substitute for the broker's
run-scoped request identity and atomic answer ownership.

The provisional `IHumanInput` and `IExternalRequestHandler` signatures are not
yet a landed type design. The final bridge must support arbitrary authored
`TRequest`/`TResponse` interactions in runtime tests while keeping the terminal's
concrete `HumanQuestion`/`HumanAnswer` contract and all MAF types inside
Infrastructure. Resolve this at the runner boundary; do not weaken public
authoring to `object` or expose `ExternalRequest` to the host.

#### Persistence Audit Map

| Mechanism | Current consumer | First-cut disposition |
| --- | --- | --- |
| `events.jsonl` / `EventStore` | Live dashboard feed and run transcript | Retain while the current dashboard tails it |
| `run.json` / `RunProjectionStore` | Standalone `publish` and current attach metadata | Retain publication fields; remove durable run ID, attach, and unused pending-request metadata |
| `sessions/<block>.json` | Previously serialized Harness sessions between agent invocations | Removed; serialized sessions now live in `PipelineRuntime` |
| `profiles/<block>.json` | Previously written by `AgentBlock`; no production reader existed | Removed; profile decisions remain in `PipelineRuntime` |
| `lifecycle/<invocation>.json` | MCP stdio child publishes an accepted capability receipt; parent agent reads it | Retain while MCP remains a subprocess boundary |
| MAF workflow state used by `PipelineInteraction` | Saves/restores the envelope around the live request port | Retain for the first in-process cut |

Each agent invocation still constructs a new Harness agent, but its serialized
session now lives entirely in `PipelineRuntime`. Lifecycle receipts are different
because they cross the retained MCP subprocess boundary.

#### Test Migration Map

Delete scheduler infrastructure and tests whose only assertion is Durable Task
fit:

- `DtsFixture`, `DurableHost`, `DurableCollection`, and
  `DurableWorkflowTestHelpers`;
- the durable half of `FitGateATests` and the durable `AddSwitch` divergence
  characterization;
- restart and rehydration cases in `FitGateCTests`, `FitGateDTests`, and
  `DurableRestartProofTests`; and
- durable closed-generic serialization assertions that have no process-lifetime
  runtime behavior to preserve.

Rewrite against the real `InProcessPipelineRunner`:

- ordered routing and loops from `FitGateBTests`;
- custom workflow-event streaming from `FitGateCTests` if current product
  observation still consumes it;
- typed request suspension and continuation from `RequestPortProbeTests`,
  `FitGateDTests`, `SupportCompositionTests`, and `HumanSuspensionProofTests`;
- agent/tool/session continuity from `FitGateDTests`,
  `DurablePlannerHandoffTests`, and the existing Debate/Delivery tests; and
- the optional real-model lifecycle proof, without scheduler setup.

Keep direct MAF characterization only where the framework contract itself is the
subject. Product acceptance tests should execute through Tandem's runner and use
real MAF in-process execution rather than duplicating its event loop.

#### Documentation And Configuration Deletions

The runtime switch must update more than README setup:

- `README.md` and `CONTRIBUTING.md` still promise restart durability and describe
  Durable Task as the runtime lifecycle;
- `docs/pipeline-authoring.md` and `docs/correctness-ledger.md` still describe
  durable handoff and restart proofs;
- `SUPPORT_SAMPLE_PLAN.md`, historical fit-gate prose, and any current operator
  guidance must stop presenting scheduler behavior as a product contract;
- `TANDEM_DTS_CONNECTION_STRING`, task-hub defaults, scheduler logging filters,
  emulator setup, and Docker scheduler requirements are removable; and
- `RunProjection.DurableRunId`, `attach`, and attach-specific architecture tests
  are removable, while standalone `publish` remains.

## Fixed Invariants

- MAF remains the workflow execution substrate.
- Tandem uses `InProcessExecution`; its adapter does not become a second
  orchestration engine.
- State-first steps, typed outcomes, typed routes, declarative agents, and
  capabilities do not change because of this runtime replacement.
- Declared failure, execution faults, and cancellation remain distinct.
- Authored request/response types remain strongly typed.
- MAF request and event types remain internal implementation details.
- Tandem owns pending human requests and answers.
- Each `tandem run` process is isolated; there is no shared process registry.
- No run-control IPC, localhost server, named pipe, shared UI, or daemon is
  introduced. Existing MCP stdio transport for agent tools is not in scope for
  removal.
- No compatibility path for existing durable runs is introduced.
- No checkpoint abstraction is added speculatively.
- The final repository has one runtime, not permanent durable and in-process
  modes.

## Runtime Design

Add a small internal adapter over MAF's maintained runner:

```csharp
internal sealed class InProcessPipelineRunner
{
    public Task<PipelineMessage<TState>> RunAsync<TState>(
        Pipeline pipeline,
        TState initialState,
        IExternalRequestHandler requests,
        CancellationToken cancellationToken
    )
        => throw new NotImplementedException();
}
```

The runner:

1. Creates the initial `PipelineMessage<TState>`.
2. Obtains the MAF workflow through `PipelineMafBridge`.
3. Calls `InProcessExecution.RunStreamingAsync`.
4. Watches the live run through `WatchStreamAsync`.
5. Extracts workflow output and framework faults. Existing block observers and
   agent updates remain the observation owners; the runner does not duplicate
   them with a general event abstraction.
6. Adapts `RequestInfoEvent` into a Tandem pending request.
7. Awaits Tandem's in-memory request handler.
8. Creates the MAF response through the original request.
9. Calls `SendResponseAsync` on the same live run.
10. Disposes the run and all pending waits on completion or cancellation.

The runner returns Tandem-owned pipeline output. A declared failed disposition
remains a value in that output. Undeclared execution faults remain exceptions,
and cancellation remains `OperationCanceledException`; do not turn either into
ordinary result cases.

No MAF event or run type crosses the internal runtime boundary.

## Human Input

Tandem owns the host-facing contract:

```csharp
public interface IHumanInput
{
    ValueTask<HumanAnswer> WaitAsync(
        PendingHumanRequest request,
        CancellationToken cancellationToken
    );
}
```

`PendingHumanRequest` and `HumanAnswer` are Tandem-owned, serializable values.
They carry run-scoped request identity and payloads without exposing
`RequestInfoEvent` or other MAF types.

Authored pipelines continue to use typed request and response values. Any type
erasure required to bridge a runtime MAF event remains inside Tandem.

`IExternalRequestHandler` in the provisional runner signature is that internal
type-erasure adapter; it is not a second public port. The final landed shape must
connect it to `IHumanInput` without exposing MAF types or weakening authored
request/response types.

## In-Memory Broker

Implement the smallest human-input adapter required by the current terminal
host using BCL primitives:

- `ConcurrentDictionary` only if the host can own more than one pending request;
- `TaskCompletionSource` with asynchronous continuations;
- `CancellationToken` for run and host shutdown; and
- optionally `Channel<T>` when it materially simplifies presenting pending
  questions to the current terminal UI.

Do not build a general broker or process registry speculatively. One CLI process
currently owns one run; support only the concurrency proven by real workflow
behavior and the acceptance tests below.

The broker must:

- generate or validate run-scoped request IDs;
- reject duplicate pending IDs;
- publish pending questions to the current `tandem run` UI;
- atomically accept one answer;
- reject wrong, duplicate, and late answers clearly;
- remove entries after answer, cancellation, or failure;
- cancel every waiter when the process shuts down; and
- retain no completed waiter or response indefinitely.

Awaiting a `TaskCompletionSource` does not occupy a worker thread. The live MAF
run and its in-memory state remain reachable until the answer arrives.

## Tandem.Tool

`tandem run` becomes the complete host for its own execution:

```text
tandem run packet.md
    |
    +-- in-process MAF run
    +-- streamed observations
    +-- in-memory pending human requests
    +-- terminal prompt/input
    +-- final result
```

The same process displays and answers its pending human requests. A second CLI
process cannot attach to or answer the run.

There is no:

- `tandem serve` daemon;
- shared pending-work panel;
- cross-process `answer` command;
- localhost control endpoint;
- process discovery;
- central run registry; or
- remote run ownership.

If a user wants concurrent runs, they start multiple `tandem run` processes.
Operating-system process isolation provides run-host isolation.

## Implementation Sequence

### 1. Characterize Required Behavior

Pin only behavior that must survive the runtime replacement:

- straight-line execution;
- typed routing and loops;
- agent execution and tools;
- declared failure;
- faults and cancellation;
- human request emission;
- human response continuation;
- streamed observations; and
- final output needed by the current command.

Restart, rehydration, task-hub isolation, and durable continuation are explicitly
not preserved.

### 2. Add The In-Process Runner

- Wrap MAF `InProcessExecution.RunStreamingAsync`.
- Adapt initial state, events, requests, responses, and outputs.
- Keep all MAF types internal.
- Return declared failure as pipeline output, propagate execution faults as
  exceptions, and propagate cancellation as `OperationCanceledException`.
- Explicitly cancel the MAF run when host cancellation is requested; cancelling
  `WatchStreamAsync` alone is insufficient.
- Initially retain the durable path only long enough to compare behavior.
- Do not expose runtime selection as a public feature.

### 3. Add In-Memory Human Input

- Add Tandem pending-request and answer types.
- Add the `IHumanInput` port.
- Add the BCL-backed in-memory implementation.
- Integrate it with the current terminal renderer/input loop.
- Ensure terminal input does not block unrelated asynchronous workflow work.

### 4. Prove The Runtime

Use real MAF in-process execution to prove:

1. A straight-line pipeline completes.
2. Typed routes and loops execute correctly.
3. Standard and custom failures retain their semantics.
4. Faults remain distinct from declared failure.
5. Cancellation ends the live run.
6. Human input becomes pending and progression stops.
7. An answer resumes the same live run with the correct state.
8. Agent sessions, usage, and tool transitions survive the wait.
9. Two runs in separate runner instances remain isolated.
10. One waiting run does not prevent another run from completing.
11. Multiple pending requests remain isolated by run and request identity.
12. Wrong, duplicate, and late answers are rejected.
13. Process/host cancellation clears every pending waiter.

Do not mock the MAF event loop in acceptance tests.

### 5. Switch Tandem.Tool

- Run pipelines exclusively through the in-process runner.
- Display pending questions in the initiating terminal.
- Accept answers in that terminal and resolve the in-memory waiter.
- Preserve final output and exit-code semantics.
- Remove `attach`; a dead or exited process cannot be reattached.
- Retain `publish` while it continues to publish a completed candidate from the
  persisted final projection.

### 6. Remove Durable Task

After real Tandem.Tool runs pass through the in-process runtime, delete:

- `Microsoft.Agents.AI.DurableTask`;
- Azure Managed Durable Task client and worker packages;
- scheduler clients and workers;
- task-hub naming and configuration;
- scheduler startup and health checks;
- scheduler Docker requirements;
- closed-generic durable workflow registration;
- durable host and worker plumbing;
- Durable Task-specific persisted suspension and restore machinery, while
  retaining the in-memory MAF state required by the current interaction;
- rehydration and orphan-recovery behavior;
- durable request continuation code;
- restart/resume CLI behavior;
- durable-only tests; and
- restart/resume documentation.

Delete the temporary dual-runtime path. In-process execution becomes the only
runtime.

### 7. Delete Durability-Driven Noise

Audit every remaining persisted concept and remove it when its only purpose was:

- replay after restart;
- workflow rehydration;
- exactly-once behavior across restart;
- durable suspension;
- orphan recovery;
- task-hub coordination;
- durable session reconstruction; or
- reconnecting to a dead workflow.

Agent sessions, usage, routing state, pending requests, and live workflow state
remain in memory unless a separate current product requirement proves otherwise.

Historical records are not retained by default merely because they already
exist. Keep a record only when Tandem currently consumes it for a user-visible
purpose, such as final candidate publication during the initiating command or a
later standalone `publish` command.

### 8. Simplify Human Authoring

Provisional status: completed by the active agent/capability refactor. Refresh
after that refactor lands.

If the landed code regresses this boundary, restore the public:

```text
Request -> Port -> Resume
```

macro as one typed wait-for-response node or capability. MAF may retain a
`RequestPort` internally, but composition must express the single semantic
operation.

This authoring cleanup is sequenced after runtime proof so it cannot hide runner
regressions.

### 9. Rewrite The Product Contract

README, CLI help, and setup documentation must state:

- runs live for the initiating process lifetime;
- human waits consume no blocked worker thread;
- independent run processes continue independently;
- only the initiating process can answer its requests;
- host exit destroys active runs;
- interrupted runs cannot resume;
- no scheduler or external runtime service is required; and
- checkpointing is not currently supported.

Remove Durable Task Scheduler from setup and operational guidance.

## Dependency Policy

Use maintained platform/framework behavior rather than building commodity
infrastructure:

- MAF `InProcessExecution` runs workflows.
- MAF `RequestPort` and `RequestInfoEvent` implement workflow request/response.
- BCL concurrency primitives implement the in-memory broker.
- The existing terminal host presents and captures answers.

Do not add MassTransit, Wolverine, MediatR, TPL Dataflow, Dapr, Hangfire,
Orleans, a queue, an actor runtime, or a custom scheduler.

## Non-Goals

This work does not add:

- durable workflow continuation;
- checkpoint persistence;
- restart recovery;
- a daemon;
- a central or shared UI;
- cross-process answers;
- local run-control IPC (existing MCP stdio tool transport remains);
- remote run control;
- leases or orphan recovery;
- a generic message bus;
- a second permanent runtime mode; or
- authoring compatibility shims for removed durable concepts.

## Completion Gate

The runtime replacement is complete when:

- Tandem.Tool uses only MAF in-process execution;
- every live run is owned by its initiating process;
- human waits are Tandem-owned and memory-backed;
- human answers resume the same live MAF run;
- concurrent independent runs remain isolated;
- cancellation cleans every run and waiter;
- no Durable Task package, scheduler, task hub, worker, or runtime configuration
  remains;
- no public authoring change was required to replace the runtime;
- no restart/resume promise remains;
- remaining persistence has a demonstrated current product purpose;
- the temporary dual-runtime implementation has been removed; and
- `task check`, `git diff --check`, and Meridian validation pass.
