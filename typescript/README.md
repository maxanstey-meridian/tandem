# Tandem for TypeScript

`@maxanstey-meridian/tandem` lets a Node application author and run Tandem pipelines in TypeScript.
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

Applications import `@maxanstey-meridian/tandem`; they do not build or load .NET assemblies
themselves.

## Packages

- `@maxanstey-meridian/tandem` is the public TypeScript API.
- `@maxanstey-meridian/tandem-runtime` selects the package for the current platform.
- `@maxanstey-meridian/tandem-runtime-darwin-arm64` contains the C# bridge and runtime files for Apple
  silicon.

Only `@maxanstey-meridian/tandem` belongs in application code.

## Author A Pipeline

Participants read and return one shared state shape. Routes decide what runs next.

```ts
import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";
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

## Agent Skills

Attach an explicitly selected existing Agent Skills or OpenCode directory:

```ts
const meridian = skill({
  directory: "/Users/max/.claude/skills/meridian",
});

const reviewer = agent({
  id: "reviewer",
  instructions: "Use the meridian skill to review the design.",
  client,
  message: (state) => state.request,
  skills: [meridian],
});
```

The runtime requires `SKILL.md` in the selected directory and delegates progressive disclosure,
`load_skill`, and read-only resource access to Microsoft Agent Framework. Tandem does not scan the
current working directory, an agent workspace, OpenCode configuration, or home directories.

Skills grant no state-transition or workspace authority. File scripts are filtered and cannot execute;
MAF still advertises its approval-gated `run_skill_script` tool when no scripts are available.

## Workspace Tools

Repository environments are reusable while each agent's access remains explicit:

```ts
const repository = agentWorkspace<State>({
  path: (state) => state.workspacePath,
  // Static catalogues are snapshotted when the workspace is defined.
  commands: [{ name: "run_tests", description: "Run the test suite.", command: "task test" }],
});

const reviewer = agent({
  id: "reviewer",
  instructions: "Review the repository.",
  client,
  message: (state: State) => state.request,
  workspace: repository.withTools([
    agentTools.always("read_file", "ls", "grep", "git:ro", repository.commands),
  ]),
});
```

`repository.commands` selects the complete fixed catalogue for that agent. A command
source function may instead derive a fresh catalogue from current state. Use separate
tool groups to give implementers conditional mutation, planners read-only inspection,
and reviewers fixed verification commands:

```ts
const executorWorkspace = repository.withTools([
  // Reads and declared checks are always available.
  agentTools.always("read_file", "ls", "grep", "git:ro", repository.commands),
  // Mutation follows application state rather than model preference.
  agentTools.when((state) => state.mutationAuthorized, "write_file", "replace"),
]);

const checkpointedWorkspace = repository.withTools(
  [agentTools.always("read_file", "ls", "grep", "git:ro", "write_file")],
  {
    // Interception is an Advanced runtime policy. Return a message to block the
    // attempted tool call, or null to allow it.
    interceptTool: async (state, invocation, { signal }) =>
      invocation.effect === "workspaceMutation" && shouldCheckpoint(state)
        ? "Call write_checkpoint before further mutation."
        : null,
  },
);

const plannerWorkspace = repository.withTools([
  // No commands and no mutation tools are selected for this role.
  agentTools.always("read_file", "ls", "grep"),
]);

const reviewerWorkspace = repository.withTools([
  // Review can inspect Git and independently rerun the declared catalogue.
  agentTools.always("read_file", "ls", "grep", "git:ro", repository.commands),
]);
```

Fixed commands do not accept model-authored command text, but they still execute without approval
and with the host process's filesystem and network authority. Selecting `"shell"` additionally lets
the model author the command text. The workspace is only a starting directory for either form of
process execution, not filesystem or network isolation.

Fixed commands do not replace authoritative pipeline verification. Agents may run them for feedback,
and output acceptance may require successful `ProcessExecution` observations, but the application
should still execute declared verification in a deterministic stage against the candidate it intends
to accept.

## Chat Clients

OpenAI-compatible clients can explicitly select or disable reasoning effort. Temperature and
maximum output tokens are authored per agent:

```ts
const client = {
  kind: "openai-compatible",
  version: 1,
  endpoint,
  model,
  wireApi: "completions",
  reasoningEffort: "none",
} as const;

const worker = agent({
  id: "worker",
  instructions: "Return one result.",
  client,
  message: (state) => state.request,
  temperature: 0,
  maxOutputTokens: 4096,
});
```

Reasoning effort accepts `"none"`, `"low"`, `"medium"`, or `"high"`. Omission means no
preference. Temperature must be between `0` and `2`; maximum output tokens must be a positive
32-bit integer. Tandem applies these settings through the maintained model-client adapters.
The bridge translates them into the same public `AgentModelRequestOptions` used by native C#
applications; it does not maintain a second request-policy implementation.

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

## Parallel Work

Use a parallel group when several agents or stages depend on the same facts but not on one another:

```ts
const classify = parallel({
  // Each named participant receives isolated state and runs concurrently.
  id: "classify-framing",
  branches: {
    world: worldClassifier,
    epistemic: epistemicClassifier,
    temporal: temporalClassifier,
  },
  // Merge is synchronous and explicit, so completion order cannot decide state.
  merge: (baseline, results) => ({
    ...baseline,
    world: results.world.world,
    epistemic: results.epistemic.epistemic,
    temporal: results.temporal.temporal,
  }),
});

const graph = pipeline({
  name: "classify-framing",
  state: FramingState,
  // Branch participants belong to classify and are not parent nodes.
  nodes: [classify, done, failed],
  start: classify,
  routes: [
    route({ from: classify, outcome: "success", to: done, label: "classified" }),
    route({ from: classify, outcome: "failed", to: failed, label: "failed" }),
  ],
  outputs: [done, failed],
});
```

A group requires at least two distinct branches and every branch must succeed before merge runs.
Branches may be agents or stages; terminals, interactions, nested parallel groups, and branch
subgraphs are not supported. Persisted branch results keep their participant IDs, while the merged
state is accepted under the group ID.

See the root [Parallel work](../README.md#parallel-work) section for the complete C# and TypeScript
semantics, including state isolation, cancellation, and side effects.

## Runtime Observation

Use the optional awaited observer for live lifecycle, agent text, reasoning, and normalized usage:

```ts
await run(graph, initialState, {
  observe: async (event, { signal }) => {
    if (event.kind === "agentUsage") {
      console.log(event.stepId, event.inputTokens, event.outputTokens);
    }
    signal.throwIfAborted();
  },
});
```

Delivery is serial and run-scoped. A slow observer applies backpressure to its run. Observer failure
faults an otherwise healthy run, while an existing execution failure or cancellation remains
authoritative. Runtime observation is separate from durable accepted-value persistence.

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

Code Writer stores accepted values in
`examples/code-writer/typescript/code-writer.sqlite3` by default. Press `q` after
completion to leave the terminal view and print the run ID and absolute ledger path.
Use `inspectAccepted` to inspect that run from application code.

## Local Tandem Studio

A TypeScript application exposes one pipeline from its nearest `tandem.config.ts`:

```ts
import { createPipeline } from "./src/pipeline.js";

export const tandem = {
  createPipeline: () => createPipeline(clients),
};
```

`createPipeline` must be synchronous and return the application's ordinary Tandem pipeline. Importing
the config and constructing that pipeline are the inspection boundary: Studio does not call `run`,
execute callbacks, or invent dependencies. The config should assemble real application dependencies
normally. Import and construction failures are shown as diagnostics. Studio loads trusted local code
with your environment and user permissions; the child process provides reload isolation, not a
security sandbox.

The debate example includes a working conventional config. From the repository root:

```sh
pnpm --dir typescript install --frozen-lockfile
(cd examples/debate/typescript && pnpm exec tandem-studio)
```

Studio discovers the nearest config from its working directory. To select one explicitly:

```sh
pnpm --dir typescript exec tandem-studio --config ../examples/debate/typescript/tandem.config.ts
```

The command starts a local browser application. Pipeline code remains authoritative; graph edits are
written to unambiguously owned TypeScript and published only after typechecking and successful pipeline
reconstruction.

## Develop

Run the complete TypeScript gate from this directory:

```sh
pnpm install --frozen-lockfile
pnpm test
```

The test builds and typechecks the SDK, checks negative type cases, runs the C# bridge
and Node integration suites, and installs the packed packages into a clean external
consumer.

Applications should await every `run` and inspection call. Completed runs release their
run-scoped callbacks and resources, so ordinary applications and `runCli` return naturally.
`runCli` sets `process.exitCode` after awaited cancellation, formatting, and output rather than
forcing process termination.

## License

[MIT](../LICENSE)

### Agent access to the run ledger

Persisting a run records history for inspection; it does not grant agents ledger tools.
`read_ledger` and `search_ledger` are disabled by default. Supply all required facts
in each agent's message unless that agent needs historical retrieval.

To explicitly grant ledger tools for a run, use `enableLedgerTools: true` alongside
`ledgerPath` in TypeScript, or `EnableLedgerTools = true` on C#
`SqlitePipelineRunOptions`. This grants access to all agents in that run. Native
callers can also explicitly attach a reader with `WithRunLedger`. Logging and
accepted-value inspection work independently of this option.
