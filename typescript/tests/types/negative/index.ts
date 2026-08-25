import {
  agent,
  agentTools,
  agentWorkspace,
  capability,
  parallel,
  pipeline,
  route,
  skill,
  stage,
  output,
  type RunOptions,
} from "@maxanstey-meridian/tandem";
import { parsePacketFile } from "@maxanstey-meridian/tandem-packets";
import { z } from "zod";
const transformedPacket = parsePacketFile(
  "---\ncount: '2'\n---",
  z.object({ count: z.string().transform(Number) }),
);
// @ts-expect-error packet values use the schema output type
const _packetCount: string = transformedPacket.value.count;
const A = z.object({ value: z.number() });
type A = z.infer<typeof A>;
const B = z.object({ value: z.string() });
type B = z.infer<typeof B>;
// @ts-expect-error stage output must preserve state
stage<A>({ id: "bad", execute: () => ({ value: "bad" }) });
const start = stage<A>({ id: "start", execute: (state) => state });
const wrong = output<B>({ id: "wrong", summary: () => "wrong" });
// @ts-expect-error routes cannot cross pipeline state types
route({ from: start, to: wrong, label: "wrong" });
const done = output<A>({ id: "done", summary: () => "done" });
const second = stage<A>({ id: "second", execute: (state) => state });
const wrongStateBranch = stage<B>({ id: "wrong-state", execute: (state) => state });
const concurrent = parallel<A>()({
  id: "concurrent",
  branches: { start, second },
  merge: (baseline: A, results) => ({ value: baseline.value + results.start.value }),
});
parallel<A>()({
  id: "unknown-merge-key",
  branches: { start, second },
  // @ts-expect-error merge results expose only authored branch keys
  merge: (baseline, results) => ({ value: baseline.value + results.missing.value }),
});
parallel<A>()({
  id: "wrong-branch-state",
  // @ts-expect-error parallel branches must preserve the group state type
  branches: { start, wrongStateBranch },
  merge: (baseline) => baseline,
});
// @ts-expect-error parallel routes must select success or failed
route({ from: concurrent, to: done, label: "ambiguous" });
parallel<A>()({
  id: "async-merge",
  branches: { start, second },
  // @ts-expect-error parallel merge is synchronous
  merge: async (baseline: A) => baseline,
});
parallel<A>()({
  id: "terminal-branch",
  // @ts-expect-error terminals cannot be parallel branches
  branches: { start, done },
  merge: (baseline: A) => baseline,
});
pipeline({
  name: "bad-initial-is-checked-at-run",
  state: A,
  nodes: [start, done],
  start,
  routes: [route({ from: start, to: done, label: "done" })],
  outputs: [done],
});
// @ts-expect-error ordinary participants cannot emit agent outcomes
route({ from: start, to: done, label: "impossible", outcome: "failed" });
// @ts-expect-error terminal participants cannot have outgoing routes
route({ from: done, to: start, label: "terminal" });
const action = capability<A, { amount: number }>({
  name: "action",
  instructions: "Perform the action.",
  schema: z.object({ amount: z.number() }),
  apply: (state) => state,
  summarize: () => "action",
});
const wrongStateAction = capability<B, { reason: string }>({
  name: "wrong",
  instructions: "Perform the wrong action.",
  schema: z.object({ reason: z.string() }),
  apply: (state) => state,
  summarize: (request) => request.reason,
});
const client = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "http://localhost:10531/v1",
  model: "test",
  wireApi: "responses",
} as const;
agent<A>({
  id: "bad-reasoning",
  instructions: "Work.",
  client,
  // @ts-expect-error reasoning effort is a closed set
  reasoning: { effort: "minimal" },
  message: () => "work",
});
agent<A>({
  id: "bad-temperature",
  instructions: "Work.",
  client,
  message: () => "work",
  // @ts-expect-error temperature must be numeric
  temperature: "cold",
});
agent<A>({
  id: "bad-output-limit",
  instructions: "Work.",
  client,
  message: () => "work",
  // @ts-expect-error output limit must be numeric
  maxOutputTokens: "many",
});
const workspace = agentWorkspace<A>({
  path: () => "/tmp",
  commands: [
    {
      name: "run_tests",
      description: "Run tests.",
      // @ts-expect-error command text must be a string
      command: 42,
    },
  ],
});
agentWorkspace<A>({
  path: () => "/tmp",
  commands: [
    {
      name: "missing-strategy",
      description: "Missing strategy.",
      command: "check",
      arguments: [
        // @ts-expect-error command arguments require one validation strategy
        { name: "value", description: "Value.", flag: "--value" },
      ],
    },
    {
      name: "two-strategies",
      description: "Two strategies.",
      command: "check",
      arguments: [
        // @ts-expect-error command arguments cannot combine validation strategies
        {
          name: "value",
          description: "Value.",
          flag: "--value",
          pattern: ".+",
          allowedValues: ["value"],
        },
      ],
    },
  ],
});
workspace.withTools([
  // @ts-expect-error workspace tools are a closed catalogue
  agentTools.always("unknown_tool"),
]);
// @ts-expect-error skill directories are strings
skill({ directory: 42 });
const worker = agent<A>({
  id: "worker",
  instructions: "Work.",
  client,
  message: () => "work",
  capabilities: [action],
});
agent<A>({
  id: "wrong-capability-state",
  instructions: "Work.",
  client,
  message: () => "work",
  // @ts-expect-error capabilities must preserve the agent state type
  capabilities: [wrongStateAction],
});
capability<A, { amount: number }>({
  name: "bad-request",
  instructions: "Use a bad request.",
  // @ts-expect-error capability request schemas and callbacks must agree
  schema: z.object({ amount: z.string() }),
  apply: (state) => state,
  summarize: () => "bad",
});
// @ts-expect-error capability instructions are required
capability<A, { amount: number }>({
  name: "missing-instructions",
  schema: z.object({ amount: z.number() }),
  apply: (state) => state,
  summarize: () => "missing",
});
capability<A, { amount: number }>({
  name: "async-contextual-validation",
  instructions: "Validate synchronously.",
  schema: z.object({ amount: z.number() }),
  // @ts-expect-error contextual validation is synchronous
  validateFor: async () => [],
  apply: (state) => state,
  summarize: () => "invalid",
});
// @ts-expect-error opaque capabilities do not expose request-specific implementation details
void action.schema;
// @ts-expect-error agent routes must select success or failed
route({ from: worker, to: done, label: "ambiguous" });
agent<A, { amount: number }>({
  id: "bad-output",
  instructions: "Work.",
  client,
  message: () => "work",
  output: {
    instructions: "Return an amount.",
    schema: z.object({ amount: z.number() }),
    // @ts-expect-error output application must preserve pipeline state
    apply: () => ({ value: "bad" }),
  },
});
agent<A, { amount: number }>({
  id: "missing-output-instructions",
  instructions: "Work.",
  client,
  message: () => "work",
  // @ts-expect-error output instructions are required
  output: { schema: z.object({ amount: z.number() }), apply: (state) => state },
});
agent<A>({
  id: "bad-client",
  instructions: "Work.",
  // @ts-expect-error unsupported wire API
  client: { ...client, wireApi: "chat" },
  message: () => "work",
});
// @ts-expect-error participant interfaces are types, not public constructors
new Stage<A>();
// @ts-expect-error CLI process exit is not exported from the root facade
import("@maxanstey-meridian/tandem").then((sdk) => sdk.closeCli(0));
// @ts-expect-error terminal is the only public presentation mode
const badPresentation: RunOptions = { presentation: "json" };
void badPresentation;
const badObserver: RunOptions = {
  // @ts-expect-error observer events are the closed RunObservation union
  observe: (event: { kind: "accepted" }) => event.kind,
};
void badObserver;
