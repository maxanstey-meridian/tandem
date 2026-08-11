import { z } from "zod";
import { output, parallel, pipeline, route, run, stage } from "../packages/sdk/dist/index.js";

const State = z.object({ values: z.array(z.string()) });
let entered = 0;
let release;
const bothEntered = new Promise((resolve) => {
  release = resolve;
});
const branch = (id) =>
  stage({
    id,
    execute: async (state) => {
      entered += 1;
      if (entered === 2) {
        release();
      }
      await bothEntered;
      return { values: [...state.values, id] };
    },
  });
const first = branch("first");
const second = branch("second");
let mergeCount = 0;
const concurrent = parallel({
  id: "concurrent",
  branches: { one: first, two: second },
  merge: (baseline, results) => {
    mergeCount += 1;
    return { values: [...baseline.values, ...results.one.values, ...results.two.values] };
  },
});
const done = output({ id: "done", summary: (state) => state.values.join(",") });
const graph = pipeline({
  name: "parallel-runtime",
  state: State,
  nodes: [concurrent, done],
  start: concurrent,
  routes: [route({ from: concurrent, outcome: "success", to: done, label: "done" })],
  outputs: [done],
});

const result = await run(graph, { values: [] });
console.log(JSON.stringify({ values: result.state.values, entered, mergeCount }));
process.exit(0);
