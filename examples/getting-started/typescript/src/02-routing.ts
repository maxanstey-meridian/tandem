import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";
import { z } from "zod";

const State = z.object({ value: z.string(), accepted: z.boolean() });
type State = z.infer<typeof State>;

const classify = stage<State>({
  id: "classify",
  execute: (state) => ({ ...state, accepted: state.value.length >= 3 }),
});
const accepted = output<State>({ id: "accepted", summary: (state) => `accepted: ${state.value}` });
const rejected = output<State>({ id: "rejected", summary: (state) => `rejected: ${state.value}` });
const example = pipeline({
  name: "route-input",
  state: State,
  nodes: [classify, accepted, rejected],
  start: classify,
  routes: [
    route({ from: classify, to: accepted, when: (state) => state.accepted, label: "accepted" }),
    route({ from: classify, to: rejected, when: (state) => !state.accepted, label: "rejected" }),
  ],
  outputs: [accepted, rejected],
});

const result = await run(example, { value: process.argv[2] ?? "Hello", accepted: false });
console.log(result.summary);
