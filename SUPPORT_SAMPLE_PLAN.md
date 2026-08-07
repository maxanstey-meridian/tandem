# Support Sample And Consumer Cleanup Plan

## Objective

Prove Tandem's complete non-coding author journey with a compiled, unprivileged
Customer Support pipeline, then use evidence from Support, Debate, and Delivery
to make only earned userland improvements.

This is a consumer-proof and organization pass. It is not another runtime or
Agent SDK redesign.

## Invariants

1. MAF remains the sole workflow, durability, model-loop, session, and tool
   substrate and remains hidden behind Tandem.
2. Delivery and every sample are unprivileged Tandem consumers.
3. `examples/` contains runnable Delivery packets. `samples/` contains example
   pipeline implementations.
4. Authored steps remain plain partial classes with nested Dunet result unions.
5. The pipeline owns typed state, semantic outcome meaning, prompts, policies,
   and routes.
6. Tandem owns execution, durability, session persistence, replay, generated
   adaptation, and framework integration.
7. Workspace capability remains opt-in. Support receives no file tools and has
   no workspace state.
8. Every agent supplies an explicit session policy.
9. No arbitrary `.WithTools(...)` is introduced without an explicit security and
   replay policy.
10. No compatibility aliases or deprecated paths are added.
11. Delivery's package boundary, topology, behavior, and correctness invariants
    do not change during its organization pass.
12. Do not abstract result mapping unless three real consumers prove a semantic
    seam rather than merely repeated syntax.

## Support Sample

Add `samples/Tandem.Sample.Support` with:

- `SupportState.cs`: durable ticket, classification, account context, proposed
  resolution, customer reply, and final disposition;
- `SupportSteps.cs`: agent-backed classification and resolution, deterministic
  account lookup, customer question, customer reply request port, reply
  application, close, and escalation;
- `SupportPrompts.cs`: role instructions and state-derived messages;
- `SupportPolicies.cs`: structured-output validation/state transitions and
  explicit session decisions;
- `SupportComposition.cs`: the complete graph and terminal outcomes;
- `SupportRegistration.cs`: explicit clients, account lookup, operations, and
  DI wiring; and
- `Tandem.Sample.Support.csproj`: Tandem plus generator references only, with no
  MAF or Delivery dependency.

The sample journey is:

```text
classify -> load account -> resolve -> ask customer -> wait for reply
                                                reply -> apply reply -> close
                                                                    -> escalate
```

Account lookup is an injected deterministic capability. It is not an agent tool
and does not require an invented database adapter. Customer reply is a real
request port so the run may suspend and resume durably.

## Required Support Proofs

1. Classification updates typed state from validated structured output.
2. Deterministic account lookup receives the classified state.
3. Resolution produces a customer-facing proposal.
4. The pipeline suspends at the customer reply request port.
5. A reply resumes the same durable state.
6. A confirming reply reaches Close.
7. A blocked reply reaches Escalate.
8. Invalid model output fails closed without mutating state.
9. Inspection reports the expected graph, port, and terminal outputs.
10. `PipelineMessage<SupportState>` serializes and round-trips.
11. The complete pipeline executes in-process.
12. Closed-generic durable execution succeeds through Durable Task Scheduler.
13. The sample imports no MAF, Delivery, Tandem infrastructure, reflection, or
    internals.
14. The sample has no workspace property or workspace configuration.

## Identity Cleanup

Each authored agent owns one constant used by both `[PipelineStage(...)]` and
`AgentRuntime.Create(...)`. Apply this to Support and Debate. Preserve Delivery's
existing centralized `BlockIds` vocabulary.

## Mapping Decision Gate

Compare the authored outcome mapping in Support, Debate, and Delivery after the
sample is complete.

Keep mappings such as:

```csharp
OutcomeKinds.Resolved => new ResolveResult.Resolved(...)
```

in userland. They express pipeline meaning and must not move into the generator.
Only add a helper if it removes mechanical state/runtime/outcome copying while
leaving the semantic mapping explicit. Otherwise retain the current shape.

## Bounded Delivery Cleanup

After Support and the mapping decision:

1. Rename Delivery-owned namespaces from legacy `Tandem.Infrastructure.*` to
   `Tandem.Delivery` without changing its package boundary.
2. Keep `DeliveryComposition` focused on graph construction and route predicates.
3. Move system instructions and state-derived user messages into
   `DeliveryPrompts`.
4. Keep session, mutation, continuation, acceptance, and teardown decisions in
   Delivery-owned policy files.
5. Inject diff acquisition instead of constructing `GitProcess` inside a policy.
6. Keep structured-output state transitions beside Delivery decision contracts.
7. Preserve the exact graph topology, lifecycle action identities, replay,
   mutation authority, checkpoint ownership, human suspension, verification, and
   publication behavior.

This cleanup does not change Tandem runtime APIs or give Delivery privileged
access.

## Documentation

Align the README's Customer Support narrative and snippets with the compiled
sample. Keep the README explanatory and readable; link to the sample for the full
implementation rather than pasting every file.

Do not add a Songwriter sample. Support is the stronger materially different
journey.

## Final Gate

Run:

1. Support focused tests;
2. Debate tests;
3. Delivery topology and policy regressions;
4. suspension, restart, replay, conflict, and durable proofs;
5. consumer and public-API architecture checks;
6. `task check`;
7. `git diff --check`; and
8. `~/Sites/plumb/plumb . --json`.

Completion means the README's support journey is compiled and tested, Delivery
remains an unprivileged consumer with clearer ownership, no unearned mapping
abstraction was introduced, and all existing behavior remains green.

## Implementation Status

Implemented:

- compiled `Tandem.Sample.Support` external consumer;
- typed classification and resolution agents with no workspace capability;
- injected deterministic account lookup;
- Tandem-owned `PipelineRequest<TState, TRequest, TResponse>` preserving pipeline
  state across typed durable request ports without userland execution-context or
  storage plumbing;
- close and escalation terminal paths;
- structured-output, invalid-output, inspection, serialization, in-process,
  request suspension/resumption, and durable closed-generic proofs;
- unprivileged consumer architecture checks;
- shared stable identity constants in Support and Debate;
- explicit semantic outcome-to-Dunet mapping retained after comparison across
  Support, Debate, and Delivery;
- Delivery consolidated under `Tandem.Delivery` with graph-only composition,
  Delivery-owned prompts and policies, injected Git/diff/workspace capabilities,
  and no Tandem infrastructure namespace imports; and
- README and authoring documentation aligned to the compiled Support journey.

No mapping helper was introduced: all three consumers showed that the switch from
semantic outcome to authored Dunet case carries pipeline meaning. The repeated
envelope fields alone did not justify moving that decision into Tandem or the
generator.
