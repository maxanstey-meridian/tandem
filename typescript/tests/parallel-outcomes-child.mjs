import { rmSync } from "node:fs";
import { z } from "zod";
import {
  inspectAccepted,
  output,
  parallel,
  pipeline,
  route,
  run,
  stage,
} from "../packages/sdk/dist/index.js";

const mode = process.argv[2];
const ledgerPath = `/tmp/tandem-sdk-parallel-${process.pid}.sqlite3`;
const State = z.object({ owner: z.string(), values: z.array(z.string()) });
const cleanup = () => {
  for (const suffix of ["", "-shm", "-wal"]) {
    rmSync(ledgerPath + suffix, { force: true });
  }
};

const makeGraph = ({ execute, merge, persist = false }) => {
  const branch = (id) =>
    stage({
      id,
      persist,
      execute: (state, context) => execute(id, state, context),
    });
  const first = branch("first");
  const second = branch("second");
  const concurrent = parallel({
    id: "concurrent",
    persist,
    branches: { one: first, two: second },
    merge,
  });
  const done = output({ id: "done", summary: (state) => state.values.join(",") });
  return pipeline({
    name: `parallel-${mode}`,
    state: State,
    nodes: [concurrent, done],
    start: concurrent,
    routes: [route({ from: concurrent, outcome: "success", to: done, label: "done" })],
    outputs: [done],
    persist,
  });
};

try {
  if (mode === "cancel") {
    let entered = 0;
    let release;
    const bothEntered = new Promise((resolve) => {
      release = resolve;
    });
    const cancellation = new AbortController();
    const events = [];
    let abortedBranches = 0;
    const graph = makeGraph({
      execute: async (_id, state, { signal }) => {
        entered += 1;
        if (entered === 2) {
          release();
        }
        const aborted = new Promise((_, reject) => {
          if (signal.aborted) {
            abortedBranches += 1;
            reject(signal.reason);
            return;
          }
          signal.addEventListener(
            "abort",
            () => {
              abortedBranches += 1;
              reject(signal.reason);
            },
            { once: true },
          );
        });
        await bothEntered;
        await aborted;
        return state;
      },
      merge: (baseline) => baseline,
    });
    const running = run(
      graph,
      { owner: "cancel", values: [] },
      { signal: cancellation.signal, observe: (event) => events.push(event) },
    );
    await bothEntered;
    cancellation.abort(new Error("cancel parallel"));
    let error;
    try {
      await running;
    } catch (caught) {
      error = caught;
    }
    console.log(
      JSON.stringify({
        entered,
        abortedBranches,
        error: String(error),
        observationCount: events.length,
      }),
    );
  } else if (mode === "invalid-merge") {
    const graph = makeGraph({
      execute: (id, state) => ({ ...state, values: [...state.values, id] }),
      merge: (baseline) => ({ ...baseline, values: [42] }),
    });
    let error;
    try {
      await run(graph, { owner: "invalid", values: [] });
    } catch (caught) {
      error = caught;
    }
    console.log(JSON.stringify({ name: error?.name, error: String(error) }));
  } else if (mode === "callback-failure") {
    const graph = makeGraph({
      execute: (id, state) => {
        if (id === "first") {
          throw new Error("parallel callback failed");
        }
        return state;
      },
      merge: (baseline) => baseline,
    });
    let error;
    try {
      await run(graph, { owner: "failure", values: [] });
    } catch (caught) {
      error = caught;
    }
    console.log(JSON.stringify({ error: String(error) }));
  } else if (mode === "persist") {
    const graph = makeGraph({
      execute: (id, state) => ({ ...state, values: [...state.values, id] }),
      merge: (baseline, results) => ({
        ...baseline,
        values: [...results.one.values, ...results.two.values],
      }),
      persist: true,
    });
    const result = await run(graph, { owner: "persist", values: [] }, { ledgerPath });
    const accepted = await inspectAccepted({ ledgerPath, runId: result.runId });
    console.log(
      JSON.stringify({
        acceptedSteps: accepted
          .filter((entry) => entry.kind === "StepCompleted")
          .map((entry) => entry.stepId)
          .sort(),
      }),
    );
  } else if (mode === "concurrent") {
    const graph = makeGraph({
      execute: (id, state) => ({ ...state, values: [...state.values, `${state.owner}-${id}`] }),
      merge: (baseline, results) => ({
        ...baseline,
        values: [...results.one.values, ...results.two.values],
      }),
    });
    const results = await Promise.all([
      run(graph, { owner: "alpha", values: [] }),
      run(graph, { owner: "beta", values: [] }),
    ]);
    console.log(JSON.stringify({ values: results.map((result) => result.state.values) }));
  } else {
    throw new Error(`Unknown mode '${mode}'.`);
  }
} finally {
  cleanup();
}
process.exit(0);
