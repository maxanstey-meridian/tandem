# Tandem for TypeScript

`@tandem/sdk` lets a Node application author and run Tandem pipelines in TypeScript.
State is described with Zod; agents, ordinary functions, interactions, and routes use
normal TypeScript values.

The TypeScript API is an authoring layer over the same C# Tandem engine used by .NET
applications. Tandem and Microsoft Agent Framework still run the graph, model loops,
sessions, capabilities, validation, and persistence. TypeScript does not contain a
second agent runtime.

This is currently an experiment for macOS arm64.

## Requirements

- macOS arm64
- Node.js 22 or newer
- .NET 10 runtime
- pnpm for this repository

Applications import `@tandem/sdk`; they do not build or load .NET assemblies
themselves.

## Packages

- `@tandem/sdk` is the public TypeScript API.
- `@tandem/runtime` selects the package for the current platform.
- `@tandem/runtime-darwin-arm64` contains the C# bridge and runtime files for Apple
  silicon.

Only `@tandem/sdk` belongs in application code.

## Author A Pipeline

Participants read and return one shared state shape. Routes decide what runs next.

```ts
import { output, pipeline, route, run, stage } from "@tandem/sdk";
import { z } from "zod";

const State = z.object({
  input: z.string(),
  normalized: z.string().nullable(),
});
type State = z.infer<typeof State>;

const normalize = stage<State>({
  id: "normalize",
  execute: (state) => ({
    ...state,
    normalized: state.input.trim().toLowerCase(),
  }),
});

const done = output<State>({
  id: "done",
  summary: (state) => state.normalized!,
});

const graph = pipeline({
  name: "normalize-input",
  state: State,
  nodes: [normalize, done],
  start: normalize,
  routes: [route({ from: normalize, to: done, label: "normalized" })],
  outputs: [done],
});

const result = await run(graph, { input: "  Hello  ", normalized: null });
console.log(result.state.normalized);
```

Agents add a chat client, instructions, a state-based message, and optional
capabilities or structured output. See the complete
[Code Writer](../examples/code-writer/typescript),
[Debate](../examples/debate/typescript), and
[Songwriter](../examples/songwriter/typescript) examples.

## Chat Clients

An agent receives a description of an OpenAI-compatible endpoint. TypeScript passes
that description to the C# host; it never sends model requests itself.

```ts
const executor = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "https://openrouter.ai/api/v1",
  model: "deepseek/deepseek-v4-flash-0731",
  wireApi: "completions",
  apiKeyEnvironmentVariable: "OPENROUTER_API_KEY",
} as const satisfies ChatClient;

const reviewer = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "http://127.0.0.1:10531/v1",
  model: "gpt-5.6-sol",
  wireApi: "responses",
  reasoningEffort: "low",
  verifyModel: true,
} as const satisfies ChatClient;
```

The examples use DS4 to create work and a local `gpt-5.6-sol` endpoint to review it.
Start that endpoint with
[`openai-oauth`](https://github.com/EvanZhouDev/openai-oauth):

```sh
npx --yes openai-oauth@latest
```

## Persistence

Set `persist: true` on a node or pipeline and provide a ledger path when running it:

```ts
const result = await run(graph, initialState, {
  ledgerPath: "pipeline.sqlite3",
});

const accepted = await inspectAccepted({
  ledgerPath: "pipeline.sqlite3",
  runId: result.runId,
});
```

`inspectAccepted` returns the structured outputs, capability calls, interactions,
failures, and stage results accepted during that run. Tandem does not store prompts,
reasoning, or streaming text, and persistence does not make a stopped run resumable.

## Run The Examples

From the repository root:

```sh
# Install once.
pnpm --dir typescript install --frozen-lockfile

OPENROUTER_API_KEY=... pnpm --dir typescript run:code-writer
OPENROUTER_API_KEY=... pnpm --dir typescript run:debate -- "Should cities remove downtown parking?"
OPENROUTER_API_KEY=... pnpm --dir typescript run:songwriter -- "A hopeful song about coming home"
```

Each command builds the experimental runtime, verifies the local Sol model, and runs
the selected example. Code Writer also requires Node.js to evaluate its generated
JavaScript in a bounded child process.

## Develop

Run the complete TypeScript gate from this directory:

```sh
pnpm install --frozen-lockfile
pnpm test
```

The test builds and typechecks the SDK, checks negative type cases, runs the C# bridge
and Node integration suites, and installs the packed packages into a clean external
consumer.

CLI programs should await every `run` and inspection call before calling `closeCli`.
The current `node-api-dotnet` host has no shutdown API, so `closeCli` exits the process;
long-running Node applications do not use it.

## License

[MIT](../LICENSE)
