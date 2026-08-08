# Agent Authoring Decision

Status: implemented.

## Decision

Tandem exposes one state-first authoring model over Microsoft Agent Framework.
MAF owns orchestration, agent loops, sessions, and tool dispatch.
Tandem owns typed composition, execution-envelope propagation, local
capabilities, interactions, and projection.

## Ordinary Authoring

- `AgentDefinition<TState>` is directly composable as a pipeline step.
- `WithMessage` receives `TState`.
- Validated typed output application is `Func<TState, TOutput, TState>`.
- A step produces only canonical `Success` or `Failed` outcomes.
- Domain decisions are facts in `TState` and routes use state predicates.
- Pass-through and state-returning stages produce canonical `Success`.
- Unhandled canonical `Failed` terminates with failed disposition.
- Exceptions remain faults and cancellation remains cancellation.
- Direct clients use `Create(id, instructions, chatClient)`.
- Agent sessions start fresh by default; `.ContinueSession()` is explicit.

There are no authored result unions, custom outcome catalogues, forwarding agent
stages, public agent operations, or raw transport transitions.

## Capabilities

An `AgentCapability<TState>` is immutable semantic configuration. Authors own:

- the semantic capability name and description;
- the typed request;
- request validation;
- a diagnostic summary; and
- the typed state transition.

Tandem owns invocation and capability identity, local MAF function binding,
structured tool errors, and atomic accepted-call handling. Advanced may decorate
the same capability with an asynchronous acceptance callback that persists a
semantic fact before Core applies the typed state transition.

`AgentCapabilities.Create` is pure. Feature composition roots may register the
returned capability as immutable application configuration, then attach it to
the intended agent. Execution follows that attachment directly; `AddTandem` does
not discover capabilities or construct a transport registry.

## Advanced Surface

Envelope-aware configuration and operations require an explicit
`Tandem.Advanced` import. This includes context messages, workspaces, custom
structured parsers, runtime-aware capability acceptance, Harness selection,
checkpoints, message augmentation, continuation policy, profile policy,
conversation policy, and `PipelineOperation`.

Advanced policies receive `AgentMessageContext<TState>` and related narrow values.
Custom blocks receive `PipelineOperationContext<TState>` and return
`OperationResult<TState>`. The broad execution envelope remains internal.

## Node ABI

First-party and ordinary consumer code use `IPipelineNode<TState>`.
`IRawPipelineNode` and raw node factories are internal.

`PipelineNodeDescriptor` and generated descriptor implementations remain public,
hidden with `EditorBrowsable(Never)`, solely because source-generated partial
classes compile in consumer assemblies. They are an opaque generated-code ABI,
not a public node hierarchy or extension seam.

## Runtime Boundary

Hosts execute through `PipelineRunner`, register typed interaction handlers in
`PipelineRunOptions`, and attach an `IPipelineObserver` to the individual run.
Observation is never captured while building a pipeline.
