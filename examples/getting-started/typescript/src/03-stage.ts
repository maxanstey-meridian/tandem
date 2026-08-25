import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";
import { z } from "zod";

const State = z.object({ value: z.string(), length: z.number().int().nonnegative() });
type State = z.infer<typeof State>;

const normalize = stage<State>({
  id: "normalize",
  execute: (state) => ({ ...state, value: state.value.trim().toLowerCase() }),
});
const measure = stage<State>({
  id: "measure",
  execute: (state) => ({ ...state, length: state.value.length }),
});
const done = output<State>({
  id: "done",
  summary: (state) => `${state.value} (${state.length} characters)`,
});
const example = pipeline({
  name: "normalize-and-measure",
  state: State,
  nodes: [normalize, measure, done],
  start: normalize,
  routes: [
    route({ from: normalize, to: measure, label: "normalized" }),
    route({ from: measure, to: done, label: "measured" }),
  ],
  outputs: [done],
});

const result = await run(example, { value: " Hello world ", length: 0 });
console.log(result.summary);
