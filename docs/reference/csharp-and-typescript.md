# C# and TypeScript

Tandem has one execution model and two authoring surfaces.

| | C# | TypeScript |
|---|---|---|
| **State** | Any typed application state | Zod schema plus its inferred type |
| **Stages** | `[PipelineStage]` classes, or `PipelineNodes.Stage(...)` | `stage(...)` |
| **Agents** | `Agent.Create<TState>(...)` | `agent<TState>(...)` |
| **Capabilities** | Typed definitions with FluentValidation validators | `capability(...)` with a Zod request schema |
| **Structured output** | Typed output definitions with validators | Zod output schemas |
| **Interactions** | `PipelineNodes.WaitFor<...>` | `interaction(...)` |
| **Parallel groups** | `PipelineNodes.Parallel(...)` | `parallel(...)` |
| **Collections** | `PipelineCollection.Create(...)` | `collection(...)` with `taskAgent(...)` |
| **Outputs** | `PipelineNodes.Complete(...)`, `PipelineNodes.Failed(...)` | `output(...)` |
| **Routes** | Fluent `Pipeline.Start(...).Route(...)` | `route(...)` inside `pipeline(...)` |
| **Running** | `PipelineRunner.RunAsync(...)` | `run(...)` |
| **Runtime** | Tandem and Microsoft Agent Framework, in-process | The same engine, through a bundled .NET bridge |

## How the TypeScript SDK works

TypeScript applications import `@maxanstey-meridian/tandem`. They don't build or load .NET
assemblies themselves. The package bundles a compiled bridge for each supported platform and
picks the right one at run time, which is why it needs the .NET 10 runtime.

Your TypeScript pipeline is translated into the same native Tandem graph a C# application
builds. Stage functions, `apply` callbacks and interaction handlers run in Node. Everything else,
including model loops, sessions, tool dispatch and the ledger, runs in the .NET engine. State
crosses between them as JSON validated against your Zod schema.

## Which is the source of truth

The C# authoring API defines Tandem's semantics. TypeScript adapts the syntax, validation and
transport, but it never owns pipeline or agent behaviour that a C# application couldn't express.
So a feature documented for one surface means the same thing on the other.
