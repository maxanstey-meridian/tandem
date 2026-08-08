# SDK Runtime Hardening Plan

## Objective

Finish Tandem's transition from an application-shaped runtime to a host-neutral
.NET pipeline SDK without reopening the state-first authoring model.

The work fixes the confirmed cross-pipeline route mutation bug, adds the public
execution boundary currently missing from the SDK, makes typed interactions
honestly in-process, executes semantic capabilities as local MAF tools, moves
observation to the live run, and then separates SDK dependencies from Tool and
Delivery dependencies.

The resulting product contract is:

- pipeline definitions and agent definitions are immutable reusable
  configuration;
- building or running one pipeline cannot alter another pipeline;
- an unprivileged .NET consumer can author and execute a pipeline through public
  Tandem APIs;
- MAF continues to own workflow orchestration, agent loops, sessions, and tool
  dispatch;
- Tandem owns the public run, interaction, observation, and capability contracts;
- `WaitFor` does not make `TState` serializable;
- semantic capabilities execute in the initiating process and do not require
  `Tandem.Tool`;
- Tool, provider, dashboard, YAML, Git, and Delivery dependencies do not leak
  into the minimal SDK package; and
- process exit still kills every live run.

## Implementation Status

Completed on 2026-08-08:

- route bindings are build-local and reusable across concurrent pipelines;
- unprivileged consumers execute through the public typed runner and interactions;
- waits preserve non-serializable in-memory state;
- observations are semantic and owned by the individual run;
- capabilities execute as invocation-local MAF functions with atomic asynchronous acceptance;
- lifecycle MCP, receipts, hidden commands, executable spawning, and discovery scans are removed;
- `Tandem`, `Tandem.Advanced`, Delivery, and Tool have explicit one-way ownership;
- the broad execution envelope is internal and Advanced exposes narrow contexts;
- direct-client agents start fresh by default and continuation is explicit;
- exported API manifests and recursive signature tests enforce package boundaries; and
- packed Songwriter, Support, and Debate consumers prove execution, analyzer delivery,
  Advanced opt-in, and dependency isolation through `task package:test`.

## Scope And Sequencing

This campaign landed before implementation of `DURABLE_LEDGER_AND_GATES_PLAN.md`,
so that plan's capability and observation hooks target the final in-process
execution seam rather than the removed MCP subprocess seam.

The ledger/gates plan is rebased onto Phase 5's local capability acceptance
contract:

- replace receipt commit/replay with the local accepted-call identity;
- register its durable write through the asynchronous post-validation,
  pre-transition acceptance seam;
- key human-answer persistence through the public interaction context's run and
  request IDs; and
- preserve its MAF function-middleware requirement.

The campaign is split into independently green phases. Do not combine the route
correctness fix with package moves, and do not begin package extraction while the
runtime contracts are still moving.

Recommended commit sequence:

1. characterize routing and fix binding-local failure awareness;
2. add the public run and typed interaction boundary;
3. remove `WaitFor` state serialization;
4. make observation run-owned and semantic;
5. replace lifecycle MCP with in-process capabilities;
6. extract Tool/Delivery/Advanced package ownership;
7. simplify the minimal agent API and harden package-consumer proofs.

## Confirmed Defects

### Shared Failure Route Awareness

`GeneratedOutcomeStepDescriptor<TState>` owns one mutable
`StandardOutcomeRouteAwareness<TState>`. Every executor bound from that
descriptor retains the same object, and every `PipelineBuilder.Build()` replaces
its matcher.

For `AgentDefinition<TState>`, which deliberately retains one descriptor, the
last pipeline build changes failure disposition behavior in already-built
pipelines. This violates definition immutability, pipeline immutability, build
isolation, and safe concurrent reuse.

The same fix should apply at the common generated-outcome binding boundary even
though generated partial stages currently return a fresh descriptor on each
property access.

### No Public Execution Boundary

`Pipeline` publicly exposes `Inspect()` but not execution. The runner, request
handler, pending request, answer, and MAF workflow bridge are internal. Tests and
`Tandem.Tool` execute only through `InternalsVisibleTo`.

An ordinary consumer can build a pipeline containing `WaitFor` but cannot run it
or answer the request using supported Tandem APIs.

### Runner Stops Consuming Events While Waiting For One Answer

`InProcessPipelineRunner` awaits request handling inside its stream enumeration.
After the first `RequestInfoEvent`, no later event is consumed until that request
is answered. MAF can halt with at least one outstanding request, so Tandem has not
proved that all requests from one halt are surfaced before any answer.

### Hidden `WaitFor` Serialization Contract

The request stage serializes the complete `PipelineMessage<TState>` into MAF
workflow state and the resume stage deserializes it. Adding one interaction
therefore silently requires all state and runtime members to round-trip through
default `System.Text.Json` behavior.

That constraint conflicts with the process-lifetime runtime contract and with
ordinary pipeline execution, where `TState` remains an in-memory object.

### Capabilities Are Not Host-Neutral

An attached capability causes `AgentBlock` to spawn:

```text
<TandemEnvironment.ExecutablePath or Environment.ProcessPath> mcp <identity>
```

Only `Tandem.Tool` implements that hidden command. Its child process reconstructs
Delivery services, not the initiating application's service provider. A custom
third-party capability therefore has no general live execution path.

Capability discovery additionally scans only `ServiceDescriptor.ImplementationInstance`,
so factory and type registrations silently disappear.

### Observation Has Split Ownership

Delivery binds some block observers through `PipelineBuildContext`, while agent
updates use a static run-ID registry. Generated stages and agents are not
consistently wrapped by the build observer. `WaitFor` is observed as private
`--request` and `--resume` executors, and generic projection contains a concrete
`HumanAnswer` type check.

Observation is a property of one execution, not graph construction or global
process state.

### Core Assembly Owns Host Infrastructure

`Tandem.csproj` directly carries MAF, FluentValidation, MCP, Hosting, OpenAI,
Spectre.Console, System.CommandLine, and YamlDotNet. Dashboard, YAML, provider
configuration, projection, lifecycle host, and Git-facing implementation types
are exported from the same assembly as the authoring API.

`System.CommandLine` has no source use in `src/Tandem`; `Tandem.Tool` uses it
transitively without a direct package reference. `Tandem.Delivery` directly
references MCP without source use.

## Fixed Invariants

### Authoring

- Keep state-first pass-through, state-updating, and standard-outcome step
  signatures.
- Keep explicit `Route` calls and canonical `Success`/`Failed` selectors.
- Keep `AgentDefinition<TState>` directly composable and reusable.
- Preserve `TState` in the built graph type as `Pipeline<TState>`; do not erase
  the state type at `Build()` and recover it only at runtime.
- Keep `PipelineNodes.WaitFor<TState,TRequest,TResponse>` as one semantic authored
  operation.
- Do not expose raw request, port, resume, MAF, or execution-envelope types to
  ordinary authors.
- Do not add a route AST or a second orchestration engine.

### Execution

- MAF `InProcessExecution` remains the only workflow runner.
- Declared failure is a result; framework/executor faults are exceptions;
  cancellation remains cancellation.
- Every run owns its state, runtime services, interactions, observations, and
  capability acceptance for its process lifetime.
- No static run registry is required for normal execution.
- No run survives process exit and no public API implies restart or resume.

### Interactions

- Authored request and response types remain strongly typed.
- MAF request IDs remain the source of request correlation.
- Tandem validates run ID, request ID, request type, and response type.
- One request accepts one response; wrong, duplicate, late, and cancelled
  responses fail clearly.
- Waiting consumes no blocked worker thread.
- `TState` is not serialized merely because a pipeline contains `WaitFor`.

### Capabilities

- Authors own typed requests, validation, summaries, and state transitions.
- Tandem owns invocation identity, tool binding, accepted-call ownership, and
  conversion to pipeline state.
- Invalid calls do not transition state and do not terminate the model turn.
- A corrected call can succeed in the same MAF session.
- The first accepted capability call terminates the active MAF turn
  mechanically.
- Accepted capabilities produce canonical agent success; routes continue to use
  typed state facts.
- MAF continues to dispatch tools through its maintained function-invocation
  middleware. Tandem does not parse model tool calls or replace the framework
  function invoker.
- MAF owns each agent request's model/function loop. Any bounded follow-up
  request for explicit structured-output correction or continuation policy must
  use a characterized maintained MAF seam and must not become a second generic
  agent loop.

### Boundaries

- Public Tandem signatures expose no MAF, MCP, Spectre, OpenAI concrete, or Tool
  infrastructure types.
- Generator ABI types are explicitly allowlisted rather than mistaken for the
  ordinary authoring API.
- Songwriter and Support consume only the minimal Tandem SDK.
- Debate and Delivery explicitly opt into Advanced APIs.
- Every project directly references packages whose types it compiles against.

## Target Runtime Shape

Exact public names should be finalized while writing the unprivileged consumer
test, but the semantic shape is fixed:

```csharp
var interactions = new PipelineInteractionHandlers()
    .Handle<CustomerQuestion, CustomerReply>(
        async (interaction, cancellationToken) =>
            await AskCustomerAsync(interaction.Request, cancellationToken)
    );

var result = await runner.RunAsync(
    pipeline,
    initialState,
    new PipelineRunOptions(
        Interactions: interactions,
        Observer: observer
    ),
    cancellationToken
);
```

The public result should expose only run-owned semantic data needed by a host:

- run ID;
- final `TState`;
- succeeded/failed disposition;
- final semantic step outcome where required by Tool diagnostics.

It should not expose `PipelineRuntime`, MAF events, `StreamingRun`, or the complete
internal `PipelineMessage<TState>`.

The public interaction registration API should be typed at registration time and
type-erased only inside Tandem. Do not make consumers implement a public
`object`/`JsonElement` request switch. A host that needs asynchronous terminal or
web presentation can register a typed handler that waits on its own broker.

Each typed handler receives a Tandem-owned interaction context containing:

- run ID;
- MAF-derived request ID;
- semantic interaction ID;
- the typed authored request; and
- the declared response type.

This is the semantic acceptance boundary for a host that must persist an answer
before returning it to the workflow. MAF types and private interaction envelopes
remain internal.

The run creates an internal `PipelineRunContext` containing:

- run identity;
- interaction dispatcher;
- semantic observer;
- capability invocation state where needed; and
- cancellation/lifetime ownership.

During Phase 2 it remains owned by the runner because the current interaction
still serializes its envelope. After Phase 3 removes that serialization, a narrow
reference may travel in the in-memory pipeline envelope for executors that need
run observation or capability acceptance. It is never serialized or persisted
and is disposed with the run.

## Phase 0: Characterize Framework Contracts

Before structural edits, add direct MAF characterization tests only where MAF is
the subject.

### Routing Characterization

Prove against pinned MAF 1.16.0:

1. Two simultaneously true conditional edges select the documented/observed
   destinations expected by Tandem.
2. Declared edge order determines first-match behavior if Tandem continues to
   promise first-match routing.
3. Two matching routes to the same target execute the target exactly once.
4. Duplicate unconditional edges fail during workflow construction.

If MAF does not provide first-match semantics, stop and change Tandem's route
construction to one MAF-native ordered switch/selection construct. Do not emulate
workflow scheduling outside MAF.

### Request Characterization

Build one real MAF workflow that produces more than one outstanding request in a
halt and prove:

- every `RequestInfoEvent` is observable before any answer is supplied;
- responses may be supplied out of order without cross-delivery;
- whether concurrent `SendResponseAsync` calls are supported;
- whether response submission needs a small internal async lock; and
- cancelling stream enumeration still requires explicit `CancelRunAsync`.

### In-Memory Interaction Envelope Characterization

Before rewriting `WaitFor`, prove a private request-envelope topology:

```text
PipelineMessage<TState>
    -> InteractionRequest<TState,TRequest>
    -> RequestPort<InteractionRequest<...>, InteractionResponse<...>>
    -> InteractionResponse<TState,TResponse>
    -> PipelineMessage<TState>
```

The request envelope carries the live pipeline message and authored request. The
response envelope carries that same live message and the typed response. Prove
that MAF preserves a deliberately non-serializable state member and that the
runner can expose only the inner authored request/response types.

If MAF's port implementation forces serialization of this private envelope, use
a run-owned continuation table keyed by `(runId, requestId)` and cleared by the
runner. Do not fall back to serializing `TState`.

### Capability Function Characterization

Use `Microsoft.Extensions.AI.AIFunction`/`AIFunctionFactory` with the real Harness
function invocation path and maintained `AsBuilder().Use(...)` middleware to
prove:

- a local function appears in `ChatOptions.Tools`;
- a flat request schema can be preserved without a synthetic `request` wrapper;
- wrong-shape input can be converted to a structured tool error;
- the interceptor sees the local function result before model serialization;
- `ficContext.Terminate = true` prevents later tools and assistant text; and
- cancellation reaches the local function.

Prefer the maintained `AIFunctionFactory` when it preserves the required flat
contract. Otherwise implement the smallest Tandem-owned `AIFunction` descriptor;
do not retain MCP merely for schema generation.

Also characterize whether MAF provides maintained hooks for the current bounded
structured-output correction and continuation requests. If it does, use them. If
it does not, explicitly document Tandem's bounded follow-up-request policy as
block behavior while leaving each request's model/function loop with MAF. Do not
leave the current unbounded-looking `while (true)` as an undocumented ownership
exception.

## Phase 1: Fix Binding-Local Route Awareness

### Tests First

Add these real-runner regressions:

1. Reuse one `AgentDefinition<TState>` in pipeline A with a failed recovery route
   and pipeline B with terminal failure. Build both before running either. Assert
   A recovers and B fails.
2. Reverse build order and assert behavior remains attached to each pipeline.
3. Use conditional failed routes with distinct state predicates and assert each
   pipeline evaluates only its own matcher.
4. Build and execute differently routed pipelines concurrently from one
   definition and assert no cross-run contamination.
5. Repeat the isolation proof at the common generated-outcome step boundary.

### Implementation

- Remove active route matcher ownership from
  `GeneratedOutcomeStepDescriptor<TState>`.
- Create one `StandardOutcomeRouteAwareness<TState>` for each builder-local
  executor binding.
- Retain that awareness beside the binding in `PipelineBuilder<TState>`.
- At `Build()`, install the matcher only into the builder-owned awareness.
- Ensure calling `Build()` again cannot mutate a previously returned `Pipeline`.
- Keep descriptors and definitions immutable after construction.

### Gate

- All route characterization and cross-build regression tests pass.
- Existing generated authoring, Songwriter, Support, Debate, and Delivery graph
  tests remain unchanged in semantics.
- `task check` passes.

## Phase 2: Add The Public Run And Interaction Boundary

### Unprivileged Test Project

Add a test/fixture assembly whose name is not covered by `InternalsVisibleTo`. It
must reference Tandem like an external consumer and must not import
`Tandem.Infrastructure` or MAF.

The first tests prove that it can:

1. build and run a straight-line generated pipeline;
2. fail at compile time when a pipeline is paired with an unrelated initial
   state type;
3. receive final state and disposition;
4. receive and answer a custom typed interaction with run/request identity;
5. observe cancellation and workflow faults distinctly; and
6. do all of this through exported Tandem APIs only.

### Public API

Add one Tandem-owned runner service and one options object. Keep overloads small:

- required: `Pipeline<TState>`, matching initial state, cancellation token;
- optional initially: run ID and typed interaction handlers;
- return: public semantic run result.

Phase 4 adds the observer to the same options object after its semantic event
contract is proven. Do not expose the current build observer temporarily.

Change `PipelineBuilder<TState>.Build()` to return `Pipeline<TState>`. Keep shared
non-generic inspection values where useful, but never permit a runner call that
can pair a workflow built for one state type with another `TState`.

Do not expose the current internal `PendingExternalRequest`,
`ExternalRequestAnswer`, or `IExternalRequestHandler` directly. They are
transport-shaped and `JsonElement`-based. Reuse their proven correlation logic
behind the typed public dispatcher.

### Runner Hardening

- Continue enumerating MAF events after publishing each request.
- Track each outstanding request-handler task by MAF request ID.
- Submit each response through the originating `ExternalRequest`.
- Serialize only `SendResponseAsync` if Phase 0 proves MAF requires it.
- Propagate request-handler faults as run faults and cancel the MAF run.
- On completion, fault, cancellation, or disposal, cancel and await every pending
  handler task and clear every broker entry.
- Reject a completed workflow that produced no final output.
- Keep all MAF event and type adaptation internal.

### Required Tests

1. Multiple requests are published before any answer.
2. Requests answered out of order resume only their matching continuations.
3. Typed handlers receive the exact run ID, request ID, interaction ID, request,
   and declared response type.
4. Wrong run/request identity is rejected.
5. Duplicate and late answers are rejected.
6. Handler failure cancels the run and clears all pending requests.
7. Cancellation while waiting cancels the MAF run and clears all pending
   requests.
8. Invalid response type faults without leaking a pending waiter.
9. One waiting run does not block another run.
10. Concurrent runs using the same built pipeline remain isolated.

### Gate

- The unprivileged consumer can run both ordinary and interactive pipelines.
- Reflection confirms no exported execution signature contains a MAF type.
- Tool can be migrated to the public runner without privileged request types.
- Existing fault/cancellation distinctions remain intact.

## Phase 3: Remove `WaitFor` State Serialization

### Implementation

- Replace workflow-state JSON save/restore in `PipelineInteraction` with the
  private in-memory request/response envelope proven in Phase 0.
- Keep the public `PipelineInteraction<TState,TRequest,TResponse>` unchanged.
- Teach the internal runner adapter to unwrap the authored request and wrap the
  typed response without exposing the private envelope.
- Keep MAF request ID as correlation authority.
- Add a semantic interaction inspection value containing interaction ID and the
  authored request/response type names. Keep `Ports` reserved for exposed raw
  ports; semantic `WaitFor` interactions must not suddenly appear there and
  private wrapper types must never appear in inspection.
- Delete the interaction-only `IPipelineExecutionContext` state APIs if no other
  production consumer remains.

### Required Tests

1. `TState` contains a non-serializable service/reference and survives a wait.
2. Object identity for an in-memory state member is preserved across resume.
3. Agent sessions, usage, profiles, and invocation counts survive resume.
4. A loop can visit the same interaction more than once without stale envelope
   reuse.
5. Concurrent runs through one interaction remain isolated.
6. Cancellation before response releases every retained envelope.
7. Public inspection shows one semantic interaction with authored types, keeps
   raw ports empty, and exposes no private wrapper executor or type name.

### Gate

- `PipelineInteraction` contains no `JsonSerializer.Serialize(pipeline)` or
  corresponding state deserialization.
- No public documentation says or implies that `TState` must be serializable.
- Existing Support and Delivery handoff behavior passes through the public
  runner.

## Phase 4: Make Observation Run-Owned And Semantic

### Public Observation Contract

Introduce one run-scoped observer contract over Tandem-owned semantic events.
The minimum event set is:

- step started;
- step completed with semantic outcome and duration;
- step faulted;
- step cancelled;
- agent update;
- interaction requested; and
- interaction answered.

Every event carries run ID and semantic step/interaction ID. Do not expose raw
MAF events, executor bindings, complete pipeline envelopes, or arbitrary generic
input/output objects.

### Runtime Ownership

- Store the observer in the internal `PipelineRunContext` created by the runner.
- Remove observer injection from `PipelineBuildContext` and Delivery graph
  construction.
- Remove the static `AgentUpdates` registry; `AgentBlock` publishes through its
  current run context.
- Apply step observation centrally to all semantic generated node forms rather
  than requiring individual node factories to accept observers.
- Emit one semantic start/completion pair for `WaitFor` using its public ID. Never
  expose `--request` or `--resume`.
- Project interaction acceptance at the interaction boundary. Remove the
  concrete `HumanAnswer` branch from generic block observation.
- Define terminal fault and cancellation callbacks so observers never retain a
  dangling started step.

### Tool Adaptation

Adapt Tool's event store/projectors and terminal renderer to the public observer.
The Tool may map semantic observations to its persisted dashboard events, but
those projections are not runtime authority.

### Required Tests

1. Every executed pass-through, state, outcome, agent, interaction, complete,
   and failed node emits one semantic lifecycle.
2. `WaitFor` emits no private physical IDs.
3. Faulted and cancelled steps emit terminal observations.
4. Generic typed interactions emit requested/answered observations without
   using `HumanQuestion` or `HumanAnswer` in core observation code.
5. Concurrent runs deliver no cross-run observations.
6. Building a pipeline captures no observer or run-specific callback.
7. Tool projections and exit behavior remain equivalent.

### Gate

- No runtime observer is supplied while building a graph.
- No global run-ID observer dictionary remains.
- Delivery composition is independent of Tool projection concerns.
- The unprivileged consumer can observe a run through only public Tandem types.

## Phase 5: Execute Capabilities In Process

### Transport-Neutral Descriptor

Refactor `AgentCapability<TState>` into immutable semantic configuration that
retains:

- tool name and description;
- typed request contract and generated schema;
- typed validation;
- summary creation;
- typed asynchronous acceptance/state transition; and
- an internal invocation binder that can produce a local `AIFunction`.

The acceptance callback receives a Tandem-owned typed context with run ID, block
ID, invocation ID, capability identity, current state, and typed request. The
existing synchronous state transition is a convenience overload wrapped in a
completed `ValueTask`. This seam allows the later ledger adapter to persist an
accepted fact before returning updated state without putting a ledger dependency
in core or carrying run identity in `TState`.

`AgentBuilder<TState>` should retain attached capability descriptors in
`AgentBlockConfig<TState>`. Do not reduce them to string names plus one composed
transition and then rediscover them through DI.

### Invocation-Scoped Local Tools

For each agent invocation:

1. Bind only the capabilities attached to that agent.
2. Capture run ID, block ID, invocation ID, and cancellation in an internal
   invocation context.
3. Add the resulting local `AIFunction` values to `ChatOptions.Tools`.
4. Validate untrusted arguments before accepting the call.
5. Return structured problems for malformed and semantically invalid input.
6. Atomically reserve the invocation's single acceptance slot before executing
   any semantic persistence or transition. Concurrent contenders receive a
   conflict and cannot run their acceptance callback.
7. Invoke the asynchronous semantic acceptance callback for the reserved call;
   any persistence failure prevents accepted tool success, state update, gate
   release, and routing.
8. On acceptance failure, release the provisional reservation so a later
   corrected call can try; on success, commit its updated state and semantic
   outcome as the invocation's accepted call.
9. Return an accepted tool result only after semantic acceptance succeeds and
   the reservation is committed.
10. Mark the MAF function invocation terminated only for an accepted result.
11. After the turn, apply the already accepted state exactly once and produce
    canonical agent success.

Name-based classification should be replaced by descriptor identity or an
invocation-local capability lookup. Reject collisions between capabilities and
built-in agent tools before model execution.

Compose capability classification, validation, semantic acceptance, outcome
collection, and termination through MAF's maintained function-invocation
middleware. Do not assign a replacement `FunctionInvokingChatClient.FunctionInvoker`.

### Receipt Disposition

The current lifecycle receipt files exist to coordinate the MCP child and parent
and replay a child-published result. Once execution is local, they have no
process-boundary purpose and do not make a dead workflow resumable.

For this greenfield runtime:

- remove generic lifecycle receipt replay from ordinary capabilities;
- identify an accepted call by `(runId, blockId, invocationId, capabilityId)` so
  a durable acceptance callback can use that identity idempotently;
- keep live accepted-state ownership invocation-local;
- remove `TandemEnvironment.ExecutablePath` and executable spawning from agent
  construction; and
- let the separate durable ledger plan persist Delivery's accepted product facts
  at its explicit acceptance boundary.

Do not create a compatibility reader or migrate old receipt files. If a concrete
external-effect capability later needs a stronger acceptance protocol, add it as
an explicit Advanced/Delivery facility rather than making every semantic tool a
subprocess.

Before deleting receipts, update `DURABLE_LEDGER_AND_GATES_PLAN.md` terminology
and acceptance tests to use the local accepted-call identity and asynchronous
acceptance callback. Remove its MCP contention assumptions, receipt replay
commit, and duplicate-receipt release tests. Preserve its actual invariant:
durable semantic acceptance completes before state transition, gate release, or
routing.

### Remove Subprocess Infrastructure

After all live capability tests pass locally, delete:

- `LifecycleMcpClient`;
- `LifecycleMcpHost`;
- `LifecycleActionSetRegistry` and registrations;
- hidden `tandem mcp` command and its environment protocol;
- capability discovery through `ImplementationInstance`;
- action-set identity used only for transport selection;
- unused MCP validation/filter infrastructure;
- `TandemEnvironment.ExecutablePath`; and
- MCP and Hosting package references that have no remaining consumer.

### Required Tests

1. A live custom Debate `submit_verdict` call succeeds without a seeded receipt,
   executable path, or Tool process.
2. Delivery `ask_planner`, `submit_report`, and `write_checkpoint` execute through
   real Harness function dispatch in process.
3. An invalid call returns structured problems, performs no transition, and can
   be corrected in the same session.
4. Wrong JSON shape returns a tool error rather than faulting the block.
5. The first accepted call terminates the turn; later tools and text do not run.
6. An error result does not terminate the turn.
7. Duplicate attachment advertises one tool and applies one transition.
8. Two concurrently attempted accepted calls execute one acceptance callback,
   produce one atomic winner and one conflict, and persist at most one semantic
   fact; do not assert scheduler-dependent winner identity.
9. Cancellation reaches the local function and leaves no accepted transition.
10. Only capabilities attached to the current agent are visible.
11. Factory/type DI registration is irrelevant because execution follows direct
    attachment, not service-descriptor discovery.
12. Checkpoint threshold, state transition, session reset, usage reset, and
    executor self-route remain intact.
13. An unknown or unattached runtime outcome fails closed rather than becoming
    canonical agent success.
14. An asynchronous acceptance failure returns no accepted tool result, performs
    no state update, and prevents workflow routing.
15. A ledger-style acceptance callback can idempotently key its write from the
    supplied run/block/invocation/capability identity.
16. Capability middleware composes with Harness file-tool invocation and does not
    replace the framework continuation.
17. Structured-output correction and configured continuation remain bounded and
    preserve one Harness session across follow-up requests.

### Gate

- No agent execution path starts a child process.
- A third-party capability runs in an unprivileged consumer host.
- `Tandem.Tool` has no hidden MCP command.
- Core and Delivery no longer reference ModelContextProtocol unless another
  independently justified feature remains.
- Mechanical MAF turn termination and typed state routing remain proven.

## Phase 6: Separate SDK, Advanced, Delivery, And Tool Ownership

Perform structural moves only after the final runtime and capability contracts
are green.

### Immediate Dependency Corrections

- Remove unused `System.CommandLine` from `Tandem.csproj`.
- Remove unused `ModelContextProtocol` from `Tandem.Delivery.csproj`.
- Add direct Tool package references for packages whose APIs Tool compiles
  against.
- Remove unused direct test package references after compiling without them.
- Remove dead implementation-only execution paths and their isolated tests only
  after confirming no production consumer.

### Tool Ownership

Move Tool-only facilities out of `Tandem.dll`:

- command-line parsing;
- YAML packet reading;
- dashboard renderer, loop, and presentation models;
- event/projection stores used only by the terminal and standalone publish;
- Tandem home/configuration loading;
- OpenAI-compatible provider construction and environment API-key reading; and
- remaining host/bootstrap code.

This should remove System.CommandLine, YamlDotNet, Spectre.Console, OpenAI,
`Microsoft.Extensions.AI.OpenAI`, and generic Hosting from the minimal Tandem
project unless a remaining SDK implementation proves ownership.

### Delivery Ownership

Move packet, publication, workspace, and Git-facing concepts that describe the
Delivery product into `Tandem.Delivery`. Tool translates YAML/input into Delivery
contracts. The generic SDK should not export Delivery's packet or publication
model.

### Physical Advanced Boundary

Create a real `Tandem.Advanced` project/package:

```text
Tandem.Advanced -> Tandem
Tandem.Delivery -> Tandem + Tandem.Advanced
Debate          -> Tandem + Tandem.Advanced
Songwriter      -> Tandem
Support         -> Tandem
```

Move advanced public extension methods and author-facing advanced policy/value
contracts there. Adapt them to narrow internal core configuration delegates
through a documented friend boundary. Core must not depend on Advanced.

Before moving public contracts, introduce core-owned internal descriptors for
each runtime concern currently represented by an Advanced public object graph:

- contextual message construction;
- workspace/tool interception;
- structured-output parsing and acceptance;
- continuation and profile selection;
- conversation retention;
- capability attachment;
- checkpoint configuration; and
- run observation/context projection.

Advanced public records and delegates map into those internal descriptors inside
the Advanced assembly. In particular, core configuration must no longer directly
name public `CheckpointPolicy<TState>`, `AgentTurnPolicy<TState>`, or other types
that are to move outward. This inversion is required before the project split;
friend access alone does not solve assembly dependency direction.

Do not move `PipelineMessage<TState>` mechanically and create a project cycle.
Instead:

- expose narrow Advanced context/observation values;
- have Advanced extension lambdas map internal envelopes through the friend
  seam; and
- make the broad pipeline envelope internal once no exported callback requires
  it.

`RouteWithContext` currently has no production consumer. Delete it unless a
concrete Advanced acceptance case proves it is needed. Do not preserve an
unearned API merely by moving it.

### Public Surface Enforcement

Add an exported API manifest grouped into:

- ordinary authoring API;
- runtime hosting API;
- Advanced API; and
- generator ABI.

Reflection tests should reject accidental exported infrastructure namespaces and
signatures referencing MAF, MCP, Spectre, OpenAI concrete, Tool, or Delivery
types. `[EditorBrowsable(Never)]` generator ABI remains public but explicitly
allowlisted.

### Gate

- Minimal Tandem project references only dependencies required by authoring and
  in-process execution.
- Songwriter and Support cannot import Advanced transitively.
- Debate and Delivery require explicit Advanced references.
- Tool directly owns all terminal/provider/configuration packages.
- No `Tandem.Infrastructure.*`, dashboard, YAML, MCP, or provider implementation
  type is exported by the minimal package.

## Phase 7: Simplify The Minimal Agent API

Do this last so ergonomics are designed against the real public runner rather
than the former Tool host.

### Direct Client Creation

Add a minimal direct-client creation path equivalent to:

```csharp
agents.Create<State>("writer", instructions, chatClient)
```

A direct client should not require a duplicate profile name or an executable
path. Profile-backed client selection and promotion belong in explicit Advanced
or host configuration.

### Session Default

Default ordinary agents to a fresh/reset session for each invocation. This is
the safe state-first behavior. Require explicit `.ContinueSession(...)` only when
conversation continuity is intentional.

Remove mandatory human-readable reasons from reset/continue decisions unless a
run observer actually publishes and consumes them. Do not retain audit-shaped
fields that have no runtime behavior or observation.

### Validation Boundary

Keep FluentValidation as an intentional public dependency during this campaign.
Do not introduce a Tandem validation abstraction without a second real validator
integration or a package-boundary requirement that earns it.

Record the coupling in the public API manifest. A later adapter is a deliberate
breaking package decision, not required to fix runtime correctness.

### Required Tests

1. The smallest Songwriter agent requires only ID, instructions, direct client,
   message, and optional typed output.
2. Omitted session configuration starts fresh and does not retain prior
   conversation.
3. Explicit continuation retains the Harness session.
4. Profile promotion remains available only through explicit Advanced/host
   configuration.
5. Ordinary API discovery contains no workspace, checkpoint, envelope, receipt,
   executable-path, or profile-client-factory machinery.

## Package Consumer Proofs

After project boundaries stabilize:

1. Pack Tandem, Tandem.Advanced, and Tandem.Generators into a temporary local
   feed.
2. Restore an isolated consumer from those packages rather than project
   references.
3. Prove the generator is delivered as an analyzer.
4. Build and run Songwriter against only Tandem.
5. Build and run Support with a live typed interaction through the public runner.
6. Build and run Debate with an in-process capability and explicit Advanced
   reference.
7. Assert no sample receives Tool, Delivery, MCP, Spectre, YAML, or OpenAI
   concrete packages unintentionally.

## Documentation Work

Update documentation in the phase that changes each contract rather than in one
final bulk pass.

Required final statements:

- definitions and built pipelines are immutable reusable configuration;
- external consumers run pipelines through the public Tandem runner;
- interactions are typed, asynchronous, process-owned, and do not serialize
  pipeline state;
- observers attach to runs, not builds;
- semantic capabilities are local MAF tools;
- no capability requires `Tandem.Tool` or a hidden command;
- the minimal and Advanced package journeys are explicit; and
- process exit still ends every active run.

Update or remove historical text that says:

- lifecycle receipts are required for an MCP subprocess;
- `PipelineBuildContext` carries execution observation;
- `TandemEnvironment.ExecutablePath` is part of agent hosting;
- `WaitFor` saves a serializable workflow continuation; or
- `Tandem.Domain` is the expected import for ordinary lifecycle policy.

## Non-Goals

- Durable workflow restart, resume, attach, or rehydration.
- A daemon, server, queue, scheduler, actor system, or second runtime.
- A custom workflow engine or custom model/tool loop.
- Backward compatibility for unpublished receipt files or current public types.
- Implementing the durable ledger or composable gates in this campaign.
- Preserving unused Advanced APIs without acceptance consumers.
- Replacing FluentValidation speculatively.
- Supporting arbitrary cross-process capability execution.
- Persisting `TState`, pending interactions, run observers, or run context.

## Risk Controls

### MAF Behavior

Pin every relied-upon routing, request, and local-function behavior with a direct
characterization test before wrapping it. Keep those tests narrowly about the
pinned SDK contract.

### Concurrency

Use real concurrent builds and runs. Do not infer isolation from object-graph
reference inspection. Every run-owned map must remove entries on success, fault,
cancellation, and disposal.

### Scope Control

Keep each phase green and reviewable. Do not combine package relocation with
behavioral runtime changes. Delete old machinery only after its replacement
passes through the real public path.

### Separate Ledger Work

Do not preserve lifecycle receipt files merely to anticipate the ledger. The
ledger plan owns durable accepted product facts and starts from its own typed
SQLite boundary. This plan owns live capability execution and removes the MCP
coordination artifact once it has no current consumer.

## Estimates

These are focused implementation estimates after investigation, not calendar
commitments:

| Phase | Estimate |
| --- | ---: |
| MAF characterization and route isolation | 1 day |
| Public runner, typed interactions, request concurrency | 2-3 days |
| `WaitFor` in-memory envelope | 1-2 days |
| Run-owned semantic observation | 2 days |
| In-process capabilities and MCP removal | 3-4 days |
| Package/Advanced extraction | 3-5 days |
| Agent ergonomics and packed consumer proofs | 2-3 days |

The route, public runner, and `WaitFor` correctness core is approximately one
focused week. Including observation and capabilities brings runtime completion
closer to two focused weeks. The complete package and ergonomics campaign is
approximately three to four focused weeks.

## Completion Gate

The campaign is complete when:

- building one pipeline cannot mutate another pipeline's route behavior;
- route ordering and duplicate-delivery assumptions are pinned against MAF;
- an unprivileged consumer can run ordinary and interactive pipelines;
- multiple outstanding requests remain isolated and clean up on every exit path;
- `WaitFor` preserves non-serializable in-memory state;
- all observation is run-owned and uses semantic IDs;
- custom capabilities execute locally through MAF without Tool or a subprocess;
- invalid capability calls can be corrected and accepted calls terminate the
  turn mechanically;
- lifecycle MCP transport, hidden command, executable path, and discovery scan
  are removed;
- the minimal package no longer owns Tool/provider/dashboard/YAML dependencies;
- Advanced is an explicit physical dependency;
- packed external samples prove authoring, execution, interactions, generators,
  and capabilities;
- README, contributing guidance, authoring docs, and correctness ledger describe
  the landed behavior;
- `task check` passes with zero warnings and errors;
- `git diff --check` passes; and
- `~/Sites/plumb/plumb . --json` reports no error-level findings.
