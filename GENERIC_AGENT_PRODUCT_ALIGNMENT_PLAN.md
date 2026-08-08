# Generic Agent Product Alignment Plan

## Objective

Align Tandem's private runtime behavior with its public product thesis:

> LLMs are bounded typed pipeline components with explicitly modeled capabilities.

The SDK hardening campaign made the runtime process-owned, typed, reusable, and
package-clean. It did not make generic agents semantically host-neutral. Every
ordinary agent is still constructed as a repository-editing Harness agent and
receives Delivery-specific coding doctrine.

This plan removes that contradiction before the public API freezes. It then
promotes semantic typed capabilities into Core, binds human interaction hosting to
semantic interaction identity, makes inspection diagrams semantic, and resolves
the remaining v1 naming, validation, and timeout decisions.

The resulting product contract is:

- a generic Tandem agent is one bounded application component, not implicitly a
  coding agent;
- generic agents receive only a tiny bounded-node contract plus authored
  instructions and attached capabilities;
- repository, workspace, mutation, evidence, planner, reviewer, packet, and
  verification doctrine belongs to Delivery;
- typed state-transition capabilities are Core authoring;
- the same Core capability may opt into a runtime-aware pre-transition acceptance
  callback from Advanced when durable acceptance requires it;
- interaction handlers bind to modeled interaction identity, not only CLR type
  pairs;
- inspection and diagrams expose one semantic graph while MAF expansion remains
  private; and
- names, namespaces, validation, and timeout behavior are deliberate before v1.

## Amendment: One Agent And One Capability Model

This plan must not create basic/advanced agents, semantic/effect agents, or
parallel capability families.

The public product model remains:

```text
Agent
  + Instructions
  + Message
  + Output
  + Capabilities
  + Session

Pipeline
  + Agent nodes
  + Code nodes
  + Routes
  + WaitFor
```

`Tandem.Advanced` is one escape hatch into runtime and execution mechanics. It
does not define a more powerful kind of agent or a different kind of capability.

Apply this ownership rule to every public API:

> If a concept describes what the agent or application is allowed to do, it
> belongs in Core. If it describes how Tandem executes, identifies, observes,
> intercepts, or durably accepts that behavior, it belongs in Advanced.

Therefore:

- `AgentCapability<TState>`, `AgentCapabilities.Create(...)`, and
  `.WithCapability(...)` belong in Core;
- capability request types, validation, summaries, and typed state transitions
  remain application-language concepts;
- run, block, invocation, and capability identity remain runtime mechanics;
- a runtime-aware durable acceptance callback remains Advanced;
- MCP, provider function calling, MAF functions, and transport are private
  implementation details; and
- no `AgentEffect`, `AdvancedCapability`, `ExternalCapability`, or similarly
  parallel taxonomy should be introduced.

This is a focused conceptual correction, not another SDK redesign. Promote the
semantic capability API, preserve the required durable acceptance seam, update
the consumers and boundary proofs, and stop.

### Amendment: Terminal Capability Semantics

An `AgentCapability<TState>` permits one validated semantic state transition.
An agent may expose multiple capabilities during a pipeline visit, but acceptance
of one capability concludes that visit. Typed output is an alternative terminal
result used only when no capability is accepted; Tandem must not parse, validate,
or correct structured output after capability acceptance.

`AgentFactory` was removed after implementation proved it held no state,
dependencies, or policy. Ordinary construction is `Agent.Create(...)`; Advanced
profile-backed construction is `AgentProfiles.Create(...)` and returns the same
Core `AgentBuilder<TState>`.

## Priority

Generic-agent neutrality is priority zero. Do not start naming cleanup or another
broad SDK redesign while Core still injects Delivery's coding worldview into
every model request.

## Confirmed Defects

### Generic Agents Are Secretly Coding Agents

`AgentBlock<TState>.CreateAgent()` always constructs `HarnessAgent` and sets:

```csharp
HarnessInstructions = TandemHarnessInstructions.Value
```

Core directly references `Microsoft.Agents.AI.Harness`, embeds root `TANDEM.md`,
and loads it for every agent. That document tells every agent that it:

- shares a workspace;
- operates on a repository;
- receives a packet;
- must investigate source and tests;
- has mutation authority;
- participates as executor, planner, reviewer, or checkpoint;
- must verify repository changes; and
- must report coding evidence.

Those instructions reach Songwriter and Support even though their authored roles
contain no repository, workspace, packet, mutation, or coding lifecycle.

This is prompt contamination, token cost, and a semantic ownership violation.
Delivery ancestry is changing unrelated application behavior through a private
runtime default.

### Pure Capabilities Are Misclassified As Advanced

The pure capability contract consists of:

- a semantic name and description;
- a typed request;
- validation;
- a diagnostic summary; and
- a `TState` plus request to `TState` transition.

That is ordinary bounded-agent authoring. It expresses that a model may cause one
typed state transition. It is as central to Tandem as typed output application.

Current package boundaries instead place `AgentCapability<TState>`,
`AgentCapabilities.Create(...)`, and `.WithCapability(...)` in
`Tandem.Advanced`, and tests deliberately exclude them from Core.

The capability remains Core even when Delivery needs durable acceptance. What is
Advanced is only the runtime-aware pre-transition acceptance policy that receives
run, block, invocation, and capability identity.

### Interaction Hosting Discards Semantic Identity

`PipelineInteraction<TState,TRequest,TResponse>` owns a stable semantic `Id`, but
`PipelineInteractionHandlers` keys registrations only by:

```text
(typeof(TRequest), typeof(TResponse))
```

Two legitimate interactions such as `legal-approval` and `security-approval`
cannot register independent handlers when they share the same CLR request and
response types. The host must use one global type handler and switch on
`InteractionId` manually.

Identity is part of the modeled operation and must remain part of handler
registration and dispatch.

`PipelineInteractionContext<TRequest,TResponse>.ResponseType` is redundant because
`TResponse` already supplies that type contract.

### Inspection Mixes Semantic Data With Physical Diagrams

`Pipeline<TState>.Inspect()` maps the private request, port, and resume expansion
back to one semantic interaction for `StepIds`, `Interactions`, and `Routes`. It
then renders Mermaid and DOT directly from the raw MAF `Workflow` through
`WorkflowVisualizer`.

The structured inspection can therefore expose one semantic interaction while
its diagrams expose private nodes such as:

```text
customer-reply--request
customer-reply
customer-reply--resume
```

Tests currently establish only that Mermaid is produced, not that private
interaction topology is absent.

### The Core Timeout Is An Undeclared Harness Policy

Every agent invocation creates a linked cancellation token and applies a fixed
ten-minute timeout. Ordinary authoring cannot configure or disable it.

The host-supplied cancellation token is the fundamental process-owned lifetime
boundary. A per-agent timeout is useful policy, not a universal runtime law.

### Pre-v1 Vocabulary Still Exposes Implementation History

- `AgentRuntime` manufactures immutable definitions; it is not a runtime.
- `TandemWorkflow.Start(...)` creates a `PipelineBuilder` for a product that calls
  its configured graphs pipelines.
- ordinary `Outcome<TState>`, `FailureEvidence`, `PipelineRunStatus`, and
  `StandardOutcomeKinds` expose `Tandem.Domain` as part of basic authoring.

These are not architectural failures, but they should be resolved before API
freeze rather than carried indefinitely for unpublished compatibility.

### Validation Ownership Is Unresolved

Core `.WithOutput<TOutput>()` and capability authoring expose
`FluentValidation.IValidator<T>` directly. This makes FluentValidation's model a
long-term part of Tandem's public compatibility contract.

FluentValidation is a sound dependency. The unresolved question is whether
Tandem's central typed acceptance boundary should be owned by Tandem or permanently
defined by one third-party validation vocabulary.

## Fixed Invariants

Do not reopen these decisions without a concrete consumer proving the need.

### Runtime And Composition

- MAF remains the only orchestration, agent-loop, session, and tool-dispatch
  substrate.
- Tandem does not introduce a second workflow graph or model/tool loop.
- Runs remain process-owned and cannot restart, resume, or attach after process
  exit.
- `PipelineRunner` remains the public hosting seam.
- Definitions and built pipelines remain immutable and concurrently reusable.

### Authoring

- An `AgentDefinition<TState>` remains directly composable as one pipeline node.
- Agents expose canonical `Success` and `Failed` execution selectors.
- Ordinary stages, prompts, output application, and route predicates remain
  state-first.
- Semantic decisions remain facts in `TState`; do not restore arbitrary authored
  result unions.
- Declared failure, undeclared faults, and cancellation remain distinct.

### Interactions And Observation

- `WaitFor` remains one semantic authored node.
- Its MAF request, port, and resume expansion remains private and in memory.
- `TState` is not serialized because a pipeline waits.
- Interaction request and response types remain strongly typed.
- Observers remain run-owned and receive semantic Tandem events.

### Durability

- Do not add generalized workflow durability.
- Do not build a daemon, scheduler, queue, broker service, or Temporal-like
  runtime.
- Durable acceptance idempotency belongs to the explicit Advanced acceptance
  implementation, not the Core capability definition or a universal effect
  guarantee.

## Target Agent Boundary

### Default Agent Execution

The default implementation should use the smallest maintained MAF agent over
`IChatClient`, expected to be `ChatClientAgent` after characterization.

Its instruction composition is:

```text
tiny generic bounded-node contract
+ authored agent instructions
+ authored state-derived user message
+ attached capability contracts
+ optional typed-output correction
```

The generic bounded-node contract should be approximately one paragraph:

> You are one bounded node in a Tandem pipeline. Follow the authored
> instructions, use only the capabilities provided for this invocation, produce
> the requested output, and return control to Tandem. A capability transition
> occurs only when Tandem reports acceptance.

It must not mention repositories, files, workspaces, packets, mutation authority,
executor/planner/reviewer roles, verification commands, or coding evidence.

### Delivery Agent Configuration

Delivery explicitly opts into a Harness-backed implementation:

```text
generic bounded-node contract
+ Delivery repository/workspace harness contract
+ authored executor/planner/reviewer role
+ dynamic packet, workspace, outcomes, constraints, and evidence
+ Delivery tools and capabilities
```

The current root `TANDEM.md` is Delivery coding doctrine. Move and rename it under
Delivery or another explicitly coding-owned package. It must stop being an
embedded Core resource.

Core must not reference `Microsoft.Agents.AI.Harness` after this split unless a
remaining generic behavior proves that dependency necessary.

This remains the same public `AgentDefinition<TState>` and
`AgentBuilder<TState>`. Do not introduce `BasicAgent`, `AdvancedAgent`,
`DeliveryAgent`, or another public agent hierarchy. Advanced configuration
changes execution mechanics behind one agent model.

## Target Capability Boundary

### Core Capability

Core owns the pure semantic form:

```csharp
public sealed class AgentCapability<TState>;

public static class AgentCapabilities
{
    public static AgentCapability<TState> Create<TState, TRequest>(
        string name,
        string description,
        /* validation */,
        Func<TRequest, string> summarize,
        Func<TState, TRequest, TState> apply
    );
}
```

`AgentBuilder<TState>.WithCapability(...)` is ordinary Core authoring.

Core continues to own invocation-local MAF binding, argument adaptation,
validation errors, one accepted call per invocation, turn termination after
acceptance, and conversion to canonical agent success.

### Advanced Durable Acceptance Policy

The durable ledger is the concrete consumer for retaining the asynchronous
acceptance seam. Its required ordering is:

```text
deserialize and validate request
    -> reserve the invocation's one acceptance slot
    -> durably accept the semantic fact idempotently
    -> apply the Core typed state transition
    -> commit the invocation-local accepted outcome
    -> return accepted tool result
    -> terminate the MAF turn
    -> route
```

The callback runs after validation and reservation but before the Core state
transition. Its Advanced-owned context includes:

- run ID;
- block ID;
- invocation ID;
- capability ID;
- current state; and
- typed request.

Do not move the capability into Advanced merely because this policy needs runtime
identity. Do not create another capability factory or type family.

The preferred API direction is an Advanced extension that decorates the same
typed Core capability, conceptually:

```csharp
var submitReport = AgentCapabilities
    .Create<DeliveryState, SubmitReportRequest>(
        "submit_report",
        description,
        validator,
        summarize,
        ApplyReport
    )
    .WithAcceptance(async (context, cancellationToken) =>
    {
        await ledger.AcceptReportAsync(
            context.AcceptedCallId,
            context.State,
            context.Request,
            cancellationToken
        );
    });
```

The exact extension name remains subject to compile-time API review, but the
shape does not: Core defines what the capability means and how accepted input
changes application state; Advanced decorates how acceptance commits before that
transition. The result remains the same immutable capability concept.

To preserve request-type inference for an Advanced extension without exposing
runtime mechanics in Core, Core may need a typed capability definition such as
`AgentCapability<TState,TRequest>` assignable to the heterogeneous
`AgentCapability<TState>` attachment contract. Treat that as a narrow C#
packaging mechanism, not a second product concept. Prove the resulting call site
before committing to the generic shape.

Do not keep the current `CreateAsync` shape unchanged merely to avoid this design
work. Its callback currently returns `TState`, which conflates the application's
state transition with the runtime's durable acceptance policy. The target split
keeps the state transition in Core and makes the Advanced callback complete only
after the durable fact commits.

The accepted-call identity is required, not speculative. The durable ledger uses
`(runId, blockId, invocationId, capabilityId)` as its idempotency key so a retry
in the same live invocation converges on the original durable fact. A dead
process remains dead; this is not workflow replay.

Document the semantic limit clearly:

- Tandem reserves one accepted call in memory for the current invocation;
- a failed acceptance callback releases that reservation;
- an external effect may have occurred before the callback throws;
- the Delivery ledger callback commits idempotently using accepted-call identity;
- retrying that identity must observe or converge on the existing fact rather
  than duplicate it; and
- Tandem does not make arbitrary external effects exactly once.

Do not build durable orchestration to hide this limitation.

## Target Interaction Boundary

The primary hosting API is identity-bound:

```csharp
var handlers = new PipelineInteractionHandlers()
    .Handle(
        support.CustomerReply,
        async (context, cancellationToken) =>
            await AskCustomerAsync(context.Request, cancellationToken)
    );
```

Passing `PipelineInteraction<TState,TRequest,TResponse>` provides semantic
identity and generic type inference.

Internally key registrations by:

```text
(interaction ID, request CLR type, response CLR type)
```

Because the API is unpublished, remove the type-only registration instead of
preserving it as a compatibility fallback.

The context retains:

- run ID;
- request ID;
- interaction ID; and
- typed request.

Remove `ResponseType`; `TResponse` is the response contract.

## Target Inspection Boundary

`PipelineInspection` is Tandem's semantic projection of the executable MAF graph.
Render Mermaid and DOT from that projection rather than from raw
`WorkflowVisualizer` output.

This does not create a second execution graph. MAF remains execution authority;
Tandem renders the public model it already exposes through:

- semantic step IDs;
- semantic interaction IDs;
- semantic routes;
- start step; and
- output steps.

No diagram may expose recognized `--request`, request-port, or `--resume`
implementation nodes.

## V1 API Direction

Finalize names only after the behavioral boundaries above are correct.

Recommended direction:

- replace the stateless `AgentRuntime`/`AgentFactory` seam with `Agent.Create(...)`;
- replace `TandemWorkflow.Start(...)` with `Pipeline.Start(...)` if the resulting
  C# call site remains unambiguous and pleasant;
- move `Outcome<TState>`, `FailureEvidence`, `PipelineRunStatus`, and
  `StandardOutcomeKinds` into the primary `Tandem` namespace; and
- avoid compatibility aliases for unpublished names.

### Validation Decision

Preferred direction: Tandem owns a small asynchronous validation contract that
returns structured validation problems, and FluentValidation is provided through
an adapter or overload in an integration package.

This seam is earned because validation is central to typed output and capability
acceptance and therefore forms part of Tandem's product contract.

Before implementing it:

1. Define the smallest semantic information Tandem actually consumes.
2. Prove both typed output and typed capabilities use the same contract.
3. Keep correction prompts and tool errors independent of validator-specific
   types.
4. Provide a FluentValidation adapter so existing validators remain pleasant.
5. Do not build a general validation framework.

If Tandem deliberately keeps `IValidator<T>` instead, record that as an explicit
v1 compatibility commitment rather than an incidental dependency.

### Timeout Decision

The host cancellation token is the default agent lifetime control.

Remove the hard-coded ten-minute timeout. Add an explicit optional policy such
as:

```csharp
.WithTimeout(TimeSpan.FromMinutes(10))
```

Omitting it means no Tandem-owned deadline beyond host cancellation. Timeout must
remain distinguishable from authored failure.

## Implementation Sequence

### Phase 0: Characterize The Generic MAF Agent

Before replacing Harness, prove the maintained generic agent seam supports the
behavior Core actually needs:

1. `IChatClient` execution and streaming updates.
2. Agent session create, serialize, deserialize, and continuation.
3. `ChatOptions.Instructions`, tools, tool mode, and response format.
4. MAF function-invocation middleware and accepted-call termination.
5. Structured-output correction in the same session.
6. Cancellation and run-owned observation.

Use `ChatClientAgent` if it satisfies those contracts. Do not retain Harness for
commodity behavior that the maintained generic agent already provides.

### Phase 1: Remove Coding-Harness Contamination

- Introduce the tiny generic bounded-node contract.
- Make generic `AgentBuilder` definitions construct the maintained generic MAF
  agent.
- Remove `TandemHarnessInstructions` and the embedded root `TANDEM.md` resource
  from Core.
- Remove Core's Harness package reference when no generic consumer remains.
- Move the repository/workspace coding contract to Delivery ownership.
- Make workspace/Harness behavior an explicit Advanced or Delivery opt-in.
- Preserve existing Delivery file tools, mutation gates, sessions, structured
  output, continuation, capabilities, and observation.
- Remove the fixed ten-minute timeout and add explicit optional timeout policy.

#### Required Tests

1. Songwriter and Support system instructions contain no repository, workspace,
   packet, mutation, planner, reviewer, or verification language.
2. A generic agent has no Harness file tools or coding providers.
3. Generic tools and typed output still execute through real MAF behavior.
4. Session reset and continuation remain correct.
5. Delivery explicitly receives its coding harness contract and file tools.
6. Delivery read/write gates and tool interception remain correct.
7. Host cancellation remains fundamental when no timeout is configured.
8. An explicit timeout cancels only the configured agent invocation and remains
   distinguishable from declared failure.
9. The packed Core consumer has no Harness package or embedded Delivery contract.

#### Gate

- Core no longer references Harness.
- Core embeds no Delivery coding instructions.
- Generic prompts are application-neutral.
- Delivery coding behavior remains explicit and green.

### Phase 2: Promote Semantic Capabilities To Core

- Move the public capability type and semantic factory into `Tandem`.
- Add Core `.WithCapability(...)`.
- Keep the immutable internal descriptor and invocation-local binder in Core.
- Retain argument-shape validation, semantic validation, one-call reservation,
  accepted-state commit, and MAF turn termination.
- Keep the typed request, summary, and `TState` transition in Core.
- Replace the current state-returning `CreateAsync` API with an Advanced-owned
  runtime acceptance decorator over the same Core capability.
- Keep accepted-call identity and the pre-transition callback in Advanced.
- Make the Advanced callback complete before Core applies its typed transition.
- Preserve reservation release on cancellation or acceptance failure so a valid
  retry may continue in the same MAF session.
- Update Debate so its pure verdict transition references only Core.
- Update Delivery's ledger-backed capabilities to add the Advanced acceptance
  decorator without changing their Core capability identity.
- Update package/API manifests and dependency tests.
- Do not rename capabilities or introduce another agent/capability taxonomy.

#### Required Tests

1. An unprivileged Core-only consumer attaches and executes a typed capability.
2. Invalid input can be corrected in the same session.
3. The accepted pure transition applies exactly once to `TState`.
4. The accepted call terminates the active MAF turn mechanically.
5. Core capability authoring exposes no run/invocation context.
6. The same Core capability can be decorated with Advanced durable acceptance.
7. Advanced acceptance receives stable run, block, invocation, and capability
   identity.
8. Durable acceptance completes before the Core state transition, accepted tool
   result, turn termination, and routing.
9. A throwing or cancelled acceptance callback produces no Tandem state
   transition and releases the live reservation.
10. A retry with the same accepted-call identity converges on one ledger fact.
11. Concurrent capability calls execute one acceptance callback and produce one
   invocation-local winner.
12. Documentation distinguishes idempotent ledger acceptance from a universal
   exactly-once external-effect guarantee.

#### Gate

- Pure capabilities require only `Tandem`.
- Advanced is required only when the same capability participates in
  runtime-aware durable acceptance or another execution policy.
- Core and Advanced expose one agent model and one capability model.
- Core remains free of Delivery and Tool concepts.

### Phase 3: Bind Interaction Handlers By Identity

- Add identity-bound `Handle(interaction, handler)` registration.
- Key registrations by interaction ID and request/response types.
- Remove type-only handler registration.
- Remove redundant `ResponseType` from the typed context.
- Preserve run ID, request ID, interaction ID, and typed request.
- Keep all MAF request and response adaptation internal.

#### Required Tests

1. Two interactions with identical request/response CLR types register distinct
   handlers.
2. Each request dispatches only to its interaction's handler.
3. Registering the same interaction twice fails clearly.
4. A handler registered from an interaction receives correct generic inference.
5. Wrong interaction identity cannot cross-deliver a response.
6. Concurrent runs through both interactions remain isolated.

#### Gate

- Hosts never inspect `InteractionId` merely to recover modeled identity lost by
  registration.
- Existing correlation, cancellation, and cleanup behavior remains green.

### Phase 4: Complete The V1 Surface

Do these as small, separately reviewable changes rather than one SDK rewrite.

1. Prove current Mermaid/DOT leakage, then render semantic diagrams.
2. Replace the unearned stateless factory with `Agent.Create(...)`.
3. Evaluate and adopt `Pipeline.Start(...)` when the call site is clear.
4. Move ordinary outcome types into `Tandem`.
5. Resolve validation ownership and provide the chosen adapter.
6. Regenerate exported API manifests and packed-consumer proofs.

#### Required Inspection Tests

1. Structured inspection contains one semantic interaction.
2. Mermaid contains the semantic interaction ID exactly once as a node.
3. DOT contains the semantic interaction ID exactly once as a node.
4. Neither format contains `--request`, `--resume`, or private wrapper types.
5. Semantic routes match the structured inspection projection.

#### Gate

- The minimal Songwriter call site uses final v1 names and only `using Tandem`.
- Core public signatures expose no `Tandem.Domain` authoring requirement.
- Validation ownership is documented as an intentional compatibility contract.
- API manifests and package-consumer tests match the frozen surface.

## Documentation Work

Update documentation in the phase that changes each contract.

Required final statements:

- generic agents are application-neutral bounded nodes;
- Delivery explicitly opts into repository/workspace Harness behavior;
- capabilities are one Core concept describing typed application behavior;
- Advanced durable acceptance decorates that same capability with runtime
  mechanics and accepted-call identity;
- ledger acceptance is idempotent but Tandem does not promise exactly-once
  arbitrary external effects;
- interaction handlers bind to semantic interaction identity;
- inspection diagrams show the semantic Tandem graph;
- host cancellation is the default lifetime boundary; and
- names, namespaces, and validation ownership are frozen deliberately.

Remove or correct statements that imply:

- every Tandem agent shares a workspace;
- every agent receives packet or repository doctrine;
- pure capabilities require Advanced;
- type-only interaction handlers preserve semantic identity;
- raw MAF visualization is the public graph; or
- every agent has an implicit ten-minute deadline.

## Non-Goals

- Reopening agent-as-node.
- Reintroducing authored outcome unions.
- Replacing MAF orchestration, sessions, tools, or model loops.
- Adding workflow durability, restart, attach, or cross-process continuation.
- Providing exactly-once external-effect execution.
- Introducing basic/advanced agents or parallel capability taxonomies.
- Building a general validation framework.
- Preserving compatibility aliases for unpublished API names.
- Refactoring Delivery internals unrelated to the generic-agent split.

## Completion Gate

This campaign is complete when:

- generic Tandem agents receive no coding, repository, workspace, packet,
  mutation, planner, reviewer, or verification doctrine;
- Core uses the maintained generic MAF agent and no longer references Harness;
- Delivery explicitly owns and installs its coding harness contract;
- no hidden default agent timeout remains;
- pure typed capabilities are available from Core;
- the same Core capability can opt into Advanced durable acceptance without
  becoming another public capability kind;
- Delivery ledger acceptance uses stable accepted-call identity idempotently and
  commits before state transition and routing;
- two same-typed semantic interactions can register independent handlers;
- `PipelineInteractionContext` contains no redundant response type;
- Mermaid and DOT expose only semantic Tandem topology;
- final agent, pipeline, namespace, and validation decisions are reflected in the
  exported API manifests;
- packed Songwriter, Support, Debate, and Delivery proofs exercise the intended
  package tiers;
- `task check` passes with zero warnings and errors;
- `git diff --check` passes; and
- Meridian validation reports no error-level findings.
