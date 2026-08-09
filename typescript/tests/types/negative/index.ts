import { agent, capability, pipeline, route, stage, output } from "@tandem/sdk";
import { z } from "zod";
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
  schema: z.object({ amount: z.number() }),
  apply: (state) => state,
  summarize: () => "action",
});
const wrongStateAction = capability<B, { reason: string }>({
  name: "wrong",
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
  // @ts-expect-error capability request schemas and callbacks must agree
  schema: z.object({ amount: z.string() }),
  apply: (state) => state,
  summarize: () => "bad",
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
  // @ts-expect-error output application must preserve pipeline state
  output: { schema: z.object({ amount: z.number() }), apply: () => ({ value: "bad" }) },
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
import("@tandem/sdk").then((sdk) => sdk.closeCli(0));
