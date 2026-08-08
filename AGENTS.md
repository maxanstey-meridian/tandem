# Tandem Agent Instructions

Before working on Tandem, read `README.md` and `CONTRIBUTING.md`.

Preserve the Core Tandem invariants in `CONTRIBUTING.md` when designing any new
abstraction. In particular:

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

Tandem is a typed agentic pipeline SDK. The configured pipeline is the lifecycle;
agents, ordinary C# stages, and human interactions are modeled graph participants,
not services called by an application-level coordinator. MAF owns live workflow
execution, agent loops, sessions, and tool dispatch. Active runs are process-owned;
do not reintroduce generalized durability.

Core versus Advanced is a semantic boundary, not a complexity tier. Core expresses
application meaning. Advanced deliberately participates in execution mechanics.
Do not put node IDs, run IDs, invocation IDs, outcomes, serialized payloads, resume
positions, or other runtime bookkeeping in `TState`.

Keep the implementation small and use the maintained framework or SDK for
commodity behavior.
