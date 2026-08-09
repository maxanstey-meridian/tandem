import {
  agent,
  capability,
  pipeline,
  route,
  stage,
  output,
  type AcceptedValue,
  type Capability,
  type Stage,
} from "@tandem/sdk";
import { closeCli } from "@tandem/sdk/cli";
import { z } from "zod";
const State = z.object({ count: z.number() });
type State = z.infer<typeof State>;
const increment = stage<State>({
  id: "increment",
  execute: (state) => ({ count: state.count + 1 }),
});
const done = output<State>({ id: "done", summary: (state) => String(state.count) });
pipeline({
  name: "positive",
  state: State,
  nodes: [increment, done],
  start: increment,
  routes: [route({ from: increment, to: done, label: "done" })],
  outputs: [done],
});
const record = capability<State, { amount: number }>({
  name: "record",
  schema: z.object({ amount: z.number() }),
  apply: (state, request) => ({ count: state.count + request.amount }),
  summarize: (request) => String(request.amount),
});
const reset = capability<State, { reason: string }>({
  name: "reset",
  schema: z.object({ reason: z.string() }),
  apply: () => ({ count: 0 }),
  summarize: (request) => request.reason,
});
const callable = z.string().transform(() => (amount: number) => amount + 1);
capability({
  name: "callable",
  schema: z.object({ apply: callable }),
  apply: (state: State, request) => ({ count: request.apply(state.count) }),
  summarize: (request) => String(request.apply(0)),
});
const granted: readonly Capability<State>[] = [record, reset];
const client = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "http://localhost:10531/v1",
  model: "test",
  wireApi: "responses",
} as const;
const worker = agent<State, { amount: number }>({
  id: "worker",
  instructions: "Work.",
  client,
  message: (state) => String(state.count),
  capabilities: granted,
  output: {
    schema: z.object({ amount: z.number() }),
    apply: (state, value) => ({ count: state.count + value.amount }),
  },
});
agent({
  id: "transforming-worker",
  instructions: "Work.",
  client,
  message: () => "work",
  output: {
    schema: z.object({ apply: callable }),
    apply: (state: State, value) => ({ count: value.apply(state.count) }),
  },
});
route({ from: worker, to: done, label: "worked", outcome: "success" });
declare const accepted: AcceptedValue;
if (accepted.kind === "CapabilityAccepted") {
  void accepted.payload;
}
const opaqueStage: Stage<State> = increment;
void opaqueStage;
void closeCli;
