import assert from "node:assert/strict";
import { setTimeout as delay } from "node:timers/promises";
import { z } from "zod";
import {
  parallel,
  pipeline,
  stage,
  output,
  route,
  run,
  inspectPipeline,
} from "../packages/sdk/dist/index.js";
const mode = process.argv[2];
const max = mode === "serial" ? 1 : 5;
const State = z.object({ owner: z.string(), values: z.array(z.number()) });
const active = new Map(),
  peaks = new Map();
let entered = 0,
  finished = 0,
  refilled = false,
  mergeCount = 0;
const controller = new AbortController();
const branches = Object.fromEntries(
  Array.from({ length: 15 }, (_, i) => [
    String(i),
    stage({
      id: `branch-${i}`,
      execute: async (state, { signal }) => {
        const n = (active.get(state.owner) || 0) + 1;
        active.set(state.owner, n);
        peaks.set(state.owner, Math.max(peaks.get(state.owner) || 0, n));
        entered++;
        if (entered > max && finished > 0 && (active.get(state.owner) || 0) > 1) {
          refilled = true;
        }
        if (mode === "cancel" && entered === max) {
          setImmediate(() => controller.abort());
        }
        try {
          if (mode === "cancel") {
            await delay(10000, null, { signal });
          } else {
            await delay(i === 0 ? 180 : 15, null, { signal });
          }
          return { ...state, values: [i] };
        } finally {
          active.set(state.owner, active.get(state.owner) - 1);
          finished++;
        }
      },
    }),
  ]),
);
const group = parallel({
  id: "limited",
  max,
  branches,
  merge: (baseline, results) => {
    mergeCount++;
    return { ...baseline, values: Object.keys(branches).flatMap((k) => results[k].values) };
  },
});
const done = output({ id: "done", summary: () => "done" });
const graph = pipeline({
  name: "parallel-max",
  state: State,
  nodes: [group, done],
  start: group,
  routes: [route({ from: group, to: done, outcome: "success", label: "done" })],
  outputs: [done],
});
let values, error;
try {
  const owners = mode === "independent" ? ["a", "b"] : ["a"];
  values = await Promise.all(
    owners.map(
      async (owner) =>
        (await run(graph, { owner, values: [] }, { signal: controller.signal })).state.values,
    ),
  );
} catch (e) {
  error = String(e);
}
if (mode === "cancel") {
  assert.equal(entered, max);
  assert.equal(mergeCount, 0);
  assert.match(error, /Abort/);
} else {
  assert.equal(error, undefined);
  for (const v of values) {
    assert.deepEqual(
      v,
      Array.from({ length: 15 }, (_, i) => i),
    );
  }
  assert.deepEqual([...peaks.values()], mode === "independent" ? [5, 5] : [max]);
  if (max > 1) {
    assert.equal(refilled, true);
  }
}
for (const bad of [0, -1, 1.5, NaN, Infinity, 2147483648]) {
  assert.throws(
    () => parallel({ id: "bad", max: bad, branches, merge: (s) => s }),
    /positive 32-bit integer/,
  );
}
assert.equal(
  [...active.values()].every((n) => n === 0),
  true,
);
console.log(
  JSON.stringify({
    peaks: [...peaks.values()],
    entered,
    finished,
    mergeCount,
    refilled,
    values,
    error,
    inspection: inspectPipeline(graph),
  }),
);
process.exit(0);
