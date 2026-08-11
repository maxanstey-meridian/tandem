import {
  agent,
  capability,
  interaction,
  interactions,
  pipeline,
  parallel,
  route,
  skill,
  stage,
  output,
  type AcceptedValue,
  type Capability,
  type Stage,
  type RunOptions,
  type RunObservation,
} from "@tandem/sdk";
import { closeCli, runCli, type RunCliOptions } from "@tandem/sdk/cli";
import { z } from "zod";
const State = z.object({ count: z.number() });
type State = z.infer<typeof State>;
const increment = stage<State>({
  id: "increment",
  execute: (state) => ({ count: state.count + 1 }),
});
const done = output<State>({ id: "done", summary: (state) => String(state.count) });
const decrement = stage<State>({
  id: "decrement",
  execute: (state) => ({ count: state.count - 1 }),
});
const concurrent = parallel<State>()({
  id: "concurrent",
  branches: { increment, decrement },
  merge: (baseline: State, results) => ({
    count: baseline.count + results.increment.count + results.decrement.count,
  }),
});
pipeline({
  name: "parallel-positive",
  state: State,
  nodes: [concurrent, done],
  start: concurrent,
  routes: [route({ from: concurrent, outcome: "success", to: done, label: "done" })],
  outputs: [done],
});
pipeline({
  name: "positive",
  state: State,
  nodes: [increment, done],
  start: increment,
  routes: [route({ from: increment, to: done, label: "done" })],
  outputs: [done],
});
const graph = pipeline({
  name: "cli-positive",
  state: State,
  nodes: [increment, done],
  start: increment,
  routes: [route({ from: increment, to: done, label: "done" })],
  outputs: [done],
});
const cliOptions: RunCliOptions<State> = {
  signal: AbortSignal.timeout(1_000),
  formatResult: async (result) => String(result.state.count),
};
void [runCli, graph, cliOptions];
const record = capability<State, { amount: number }>({
  name: "record",
  instructions: "Record an amount.",
  schema: z.object({ amount: z.number() }),
  apply: (state, request) => ({ count: state.count + request.amount }),
  summarize: (request) => String(request.amount),
});
const reset = capability<State, { reason: string }>({
  name: "reset",
  instructions: "Reset with a reason.",
  schema: z.object({ reason: z.string() }),
  apply: () => ({ count: 0 }),
  summarize: (request) => request.reason,
});
const callable = z.string().transform(() => (amount: number) => amount + 1);
capability({
  name: "callable",
  instructions: "Apply a callable.",
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
const meridian = skill({ directory: "/skills/meridian" });
const worker = agent<State, { amount: number }>({
  id: "worker",
  instructions: "Work.",
  client,
  message: (state) => String(state.count),
  capabilities: granted,
  skills: [meridian],
  output: {
    instructions: "Return an amount.",
    schema: z.object({ amount: z.number() }),
    validateFor: (state, value) =>
      value.amount >= state.count ? [] : [{ path: "$.amount", message: "too small" }],
    apply: (state, value) => ({ count: state.count + value.amount }),
  },
});
agent({
  id: "transforming-worker",
  instructions: "Work.",
  client,
  message: () => "work",
  output: {
    instructions: "Return a callable.",
    schema: z.object({ apply: callable }),
    apply: (state: State, value) => ({ count: value.apply(state.count) }),
  },
});
const review = interaction({
  id: "review",
  requestSchema: z.object({ count: z.number() }),
  responseSchema: z.object({ accepted: z.boolean() }),
  request: (state: State) => ({ count: state.count }),
  apply: (state, response) => ({ count: response.accepted ? state.count : 0 }),
});
const handlers = interactions().handle(review, (request, { signal }) => ({
  accepted: request.count > 0 && !signal.aborted,
}));
void handlers;
route({ from: worker, to: done, label: "worked", outcome: "success" });
declare const accepted: AcceptedValue;
if (accepted.kind === "CapabilityAccepted") {
  void accepted.payload;
}
const opaqueStage: Stage<State> = increment;
void opaqueStage;
const terminalPresentation: RunOptions = { presentation: "terminal" };
void terminalPresentation;
const observe = (event: RunObservation, { signal }: { readonly signal: AbortSignal }) => {
  signal.throwIfAborted();
  switch (event.kind) {
    case "stepStarted":
    case "stepCompleted":
    case "stepCancelled":
      void event.stepId;
      break;
    case "stepFaulted":
      void event.error;
      break;
    case "agentText":
    case "agentReasoning":
      void event.text;
      break;
    case "agentUsage":
      void (event.inputTokens + event.outputTokens + event.currentContextTokens);
      break;
  }
};
const observedRun: RunOptions = { observe };
void observedRun;
void closeCli;
