# How It Works

Tandem is a typed application model over Microsoft Agent Framework (MAF). It's not a second
workflow engine. Every route you declare becomes a real MAF edge as you build, and MAF runs the
graph.

## Who owns what

Tandem owns the application-facing model:

- typed state
- participants and agent definitions
- structured outputs and capabilities
- interactions
- parallel branches with deterministic merge, and collections
- semantic routes and outputs
- persistence of accepted values

Microsoft Agent Framework owns the live execution underneath:

- workflow execution
- model loops
- sessions
- tool dispatch
- workflow events

Those mechanics stay below Tandem's ordinary authoring surface. Features that deliberately need
to take part in them live in the separate Advanced package, so ordinary pipelines never have to
understand runtime envelopes, executor bindings, provider transport or framework node
identities.

## The placement test

When Tandem gains a concept, one question decides where it lives. Could an application author
reasonably invent this concept while describing their system, without knowing how Tandem is
implemented?

- **Yes:** it can belong in Core, alongside state, agents, capabilities, routes and
  interactions.
- **No**, because it exists only because of Tandem, MAF, a provider, persistence or transport:
  it stays private, or goes in Advanced.

If a feature forces ordinary authoring back into JSON parsing, string outcome kinds, runtime
envelopes or node identities, the abstraction is at the wrong level.

## Model output is untrusted

Everything a model writes is treated as untrusted input at a boundary:

- Capability calls are validated before acceptance. Invalid calls return a structured tool error
  that the model can correct in the same session.
- One capability call owns acceptance atomically. Acceptance completes before the state
  transition, before the turn ends, and before routing.
- Structured output must pass syntax, shape, enum and cross-field validation before it can affect
  state or routing. It gets one corrective response, then fails closed.
- Workspace tool authority is classified by effect (read, mutation, process execution). Where an
  authority gate is active, unclassified tools fail closed.

## Process-owned runs

A run executes in the process that started it. Tandem deliberately doesn't offer generalised
durability. There are no resumable runs, checkpoints to disk or replay. The ledger is a journal
of what a run accepted, kept for inspection.
