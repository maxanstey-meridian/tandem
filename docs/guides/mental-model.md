# The Mental Model

A Tandem application is built from a small set of pieces. If you know typed objects,
functions, function calling and `if` statements, you already know most of the ideas.

![The Tandem mental model](/tandem-mental-model.svg)

| Piece | Think of it as | What it means |
|---|---|---|
| **State** | The facts | Your application's typed lifecycle state: a C# type or a Zod schema |
| **Participant** | A box that gets a turn | The common idea behind agents, stages and interactions |
| **Agent** | A model-backed participant | Receives instructions and a message built from current state |
| **Stage** | A deterministic participant | Runs a normal operation and may return updated state |
| **Parallel group** | Independent work, joined | Runs named agents or stages on isolated state, then explicitly merges their results |
| **Collection** | Work over a runtime-sized list | Runs declared agents once per item, with bounded concurrency |
| **Capability** | A typed action | Something an agent is explicitly permitted to do |
| **Interaction** | A typed handoff | Waits for an external request and response before continuing |
| **Route** | An arrow, or an `if` | Explicitly decides which participant runs next |
| **Output** | An end point | A named successful or failed terminal |
| **Outcome** | Did this participant execute? | `Success` or `Failed`, kept separate from domain decisions |

## The rules

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

- **Facts in state.** Every participant reads and returns the same application-owned state.
  It holds what your application knows, never Tandem's bookkeeping.
- **Decisions in routes.** Participants don't choose their successors. Routes read state and
  pick the next participant.
- **Permissions in capabilities.** An agent acts on your application only through typed,
  validated capabilities you attach to it.
- **Humans in interactions.** Anything outside the process, whether a person, a UI or another
  system, is a typed interaction that the host answers.
- **Runtime mechanics below the seam.** Model loops, sessions, tool transport and execution
  bookkeeping belong to Tandem and Microsoft Agent Framework, not to your pipeline.

## How a run executes

1. Start with the caller's initial typed state.
2. Run the current participant.
3. Accept its validated result or state transition.
4. Evaluate that participant's outgoing routes in order.
5. Follow the first matching route.
6. Run the next participant.
7. Repeat until the run completes, fails or is cancelled.

A parallel group or a collection runs its own work, then returns one ordinary outcome to the
same route cycle. An interaction waits for its answer in-process, then carries on.

There is no second, application-level orchestration model behind the graph. **The configured
pipeline is the lifecycle.**

## The running example

The guides use one example throughout: **Code Writer**. An implementer agent writes a function,
a deterministic stage verifies it, and a reviewer agent accepts it or requests changes. Failed
verification and requested changes loop back to the implementer.

```text
implementer ──submitted──▶ verification ──passed──▶ reviewer ──accepted──▶ done
     ▲                          │                       │
     └────────failed────────────┘◀───changes requested──┘
```

Its full source is in the
[examples](https://github.com/maxanstey-meridian/tandem/tree/main/examples/code-writer).
