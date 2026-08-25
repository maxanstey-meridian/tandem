import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";
import { resolve } from "node:path";
import { z } from "zod";

const State = z.object({ value: z.string() });
type State = z.infer<typeof State>;

const normalize = stage<State>({
  id: "normalize",
  persist: true,
  execute: (state) => ({ ...state, value: state.value.trim().toLowerCase() }),
});
const done = output<State>({ id: "done", summary: (state) => state.value });
const example = pipeline({
  name: "persistent-normalization",
  state: State,
  nodes: [normalize, done],
  start: normalize,
  routes: [route({ from: normalize, to: done, label: "normalized" })],
  outputs: [done],
  persist: true,
});
const ledgerPath = resolve("getting-started.sqlite3");
const result = await run(example, { value: " Hello " }, { ledgerPath });

console.log(`Run ${result.runId} recorded in ${ledgerPath}`);
