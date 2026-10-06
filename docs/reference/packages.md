# Packages

## NuGet

C# applications need the .NET 10 SDK.

| Package | Provides | Needed by |
|---|---|---|
| `Meridian.Tandem` | State, agents, capabilities, interactions, parallel groups, collections, routes and `PipelineRunner` | Every C# application |
| `Meridian.Tandem.Generators` | The source generator behind `[PipelineStage]` classes. A development dependency, so it isn't part of your published API | [Stages](/guides/stages) |
| `Meridian.Tandem.Ledger` | The SQLite run ledger: `SqlitePipelineRunOptions`, `SqliteLedgerStore` | [Persistence](/guides/persistence) |
| `Meridian.Tandem.Packets` | `PacketFile`, for Markdown with YAML frontmatter | [Packet Files](/guides/packets) |
| `Meridian.Tandem.Advanced` | `AgentWorkspace`, `AgentTools`, raw output, output acceptance and other execution-mechanics APIs | [Workspace Tools](/guides/workspace-tools) |
| `Meridian.Tandem.OpenAICompatible` | Provider normalisation and retries for OpenAI-compatible endpoints | [Chat Clients](/reference/chat-clients) |
| `Meridian.Tandem.Terminal` | The live terminal view | [Running a Pipeline](/guides/running#the-terminal-view) |

```sh
dotnet add package Meridian.Tandem
dotnet add package Meridian.Tandem.Generators
```

## npm

TypeScript applications need Node.js 22 or newer and the .NET 10 runtime, on macOS (Apple
silicon) or Linux x64.

| Package | Provides |
|---|---|
| `@maxanstey-meridian/tandem` | The TypeScript authoring API and the bundled .NET bridge for each supported platform |
| `@maxanstey-meridian/tandem-packets` | `readPacketFile`, for Markdown with YAML frontmatter |
| `@maxanstey-meridian/tandem-studio` | [Tandem Studio](/guides/studio), the local pipeline viewer |

```sh
npm install @maxanstey-meridian/tandem zod
```

`zod` is a peer dependency. The package is ESM-only. It ships compiled JavaScript, type
declarations and the bridge, so installing it doesn't compile anything or download a runtime.

## Core and Advanced

`Meridian.Tandem` is **Core**: it describes what your application and its agents mean.
`Meridian.Tandem.Advanced` is for deliberately taking part in how Tandem executes those things,
such as workspace authority, provider options, invocation context and low-level policy hooks.

The split is semantic, not a complexity tier. A sophisticated agent can live entirely in Core.
Reach for Advanced only when you mean to touch the machinery.
