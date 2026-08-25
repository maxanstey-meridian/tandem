import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";
import { z } from "zod";

const State = z.object({ value: z.string() });
type State = z.infer<typeof State>;

const normalize = stage<State>({
  id: "normalize",
  execute: (state) => ({ ...state, value: state.value.trim() }),
});
const done = output<State>({ id: "done", summary: (state) => state.value });
const example = pipeline({
  name: "single-pipeline",
  state: State,
  nodes: [normalize, done],
  start: normalize,
  routes: [route({ from: normalize, to: done, label: "normalized" })],
  outputs: [done],
});

const result = await run(example, { value: " Hello " });
console.log(result.state.value);
