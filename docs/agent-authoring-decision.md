# Agent Authoring Decision

Status: implemented.

## Decision

Tandem exposes one state-first authoring model over Microsoft Agent Framework.
MAF owns orchestration, durability, agent loops, sessions, and tool dispatch.
Tandem owns typed composition, execution-envelope propagation, capabilities,
receipts, interactions, and projection.

## Ordinary Authoring

- `AgentDefinition<TState>` is directly composable as a pipeline step.
- `WithMessage` receives `TState`.
- Validated typed output application is `Func<TState, TOutput, TState>`.
- A step produces only canonical `Success` or `Failed` outcomes.
- Domain decisions are facts in `TState` and routes use state predicates.
- Pass-through and state-returning stages produce canonical `Success`.
- Unhandled canonical `Failed` terminates with failed disposition.
- Exceptions remain faults and cancellation remains cancellation.

There are no authored result unions, custom outcome catalogues, forwarding agent
stages, public agent operations, or raw receipt transitions.

## Capabilities

An `AgentCapability<TState>` is immutable semantic configuration. Authors own:

- the semantic capability name and description;
- the typed request;
- request validation;
- a diagnostic summary; and
- the typed state transition.

Tandem derives and owns action-set identity, receipt identity, request/payload
serialization, MCP registration, persistence, duplicate-call handling, replay
deserialization, and transport.

`AgentCapabilities.Create` is pure. Feature composition roots register the
returned capability as application configuration. `AddTandem` discovers those
instances and constructs the lifecycle transport registry internally.

## Advanced Surface

Envelope-aware configuration and operations require an explicit
`Tandem.Advanced` import. This includes context messages, workspaces, custom
structured parsers, capabilities, checkpoints, message augmentation, continuation
policy, profile policy, conversation policy, and `PipelineOperation`.

`PipelineMessage<TState>`, `BlockOutcome`, `OperationResult<TState>`, and internal
string evidence kinds remain legitimate advanced execution concepts. They are not
ordinary routing vocabulary.

## Node ABI

First-party and ordinary consumer code use `IPipelineNode<TState>`.
`IRawPipelineNode` and raw node factories are internal.

`PipelineNodeDescriptor` and generated descriptor implementations remain public,
hidden with `EditorBrowsable(Never)`, solely because source-generated partial
classes compile in consumer assemblies. They are an opaque generated-code ABI,
not a public node hierarchy or extension seam.

## Runtime Boundary

`PipelineBuildContext` remains public for host observation. It is not required to
build agents. Its final placement should be reconsidered with the planned runtime
replacement rather than changed independently.
