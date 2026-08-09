import { z } from "zod";
import {
  ContractValidationError,
  interaction,
  interactions,
  output,
  pipeline,
  route,
  run,
  stage,
} from "../packages/sdk/dist/index.js";

const AnyState = z.object({ value: z.any() });
const done = output({ id: "done", summary: () => "done" });
const pass = stage({ id: "pass", execute: (state) => state });
const graph = pipeline({
  name: "json-boundaries",
  state: AnyState,
  nodes: [pass, done],
  start: pass,
  routes: [route({ from: pass, to: done, label: "done" })],
  outputs: [done],
});

const hidden = { visible: true };
Object.defineProperty(hidden, "hidden", { value: true, enumerable: false });
const cyclic = {};
cyclic.self = cyclic;
const sparse = [];
sparse.length = 1;
const arrayWithHiddenState = [];
Object.defineProperty(arrayWithHiddenState, "hidden", { value: true, enumerable: false });
const symbolProperty = { visible: true };
symbolProperty[Symbol("hidden")] = true;
class Instance {
  value = 1;
}
const cases = {
  nan: Number.NaN,
  infinity: Number.POSITIVE_INFINITY,
  bigint: 1n,
  undefinedProperty: { missing: undefined },
  undefinedArrayEntry: [undefined],
  sparse,
  arrayWithHiddenState,
  date: new Date("2026-01-01T00:00:00.000Z"),
  instance: new Instance(),
  symbolProperty,
  hidden,
  toJSON: { value: 1, toJSON: () => ({ value: 2 }) },
  cyclic,
};

const results = {};
for (const [name, value] of Object.entries(cases)) {
  try {
    await run(graph, { value });
    results[name] = { succeeded: true };
  } catch (error) {
    results[name] = {
      name: error.name,
      error: String(error),
      cause: error.cause ? String(error.cause) : null,
      boundary: error.boundary,
      contract: error instanceof ContractValidationError,
      problem: error.problems?.[0],
    };
  }
}

const capture = async (name, operation) => {
  try {
    await operation();
    results[name] = { succeeded: true };
  } catch (error) {
    results[name] = {
      name: error.name,
      error: String(error),
      cause: error.cause ? String(error.cause) : null,
      boundary: error.boundary,
      contract: error instanceof ContractValidationError,
      problem: error.problems?.[0],
    };
  }
};

await capture("stageOutput", async () => {
  const bad = stage({ id: "bad-stage", execute: () => ({ value: Number.NaN }) });
  const badGraph = pipeline({
    name: "bad-stage-output",
    state: AnyState,
    nodes: [bad, done],
    start: bad,
    routes: [route({ from: bad, to: done, label: "done" })],
    outputs: [done],
  });
  await run(badGraph, { value: 0 });
});

const interactionGraph = (request, apply) => {
  const ask = interaction({
    id: "ask",
    requestSchema: z.object({ value: z.any() }),
    responseSchema: z.object({ value: z.any() }),
    request,
    apply,
  });
  return {
    ask,
    graph: pipeline({
      name: "bad-interaction-boundary",
      state: AnyState,
      nodes: [ask, done],
      start: ask,
      routes: [route({ from: ask, to: done, label: "done" })],
      outputs: [done],
    }),
  };
};

await capture("interactionRequest", async () => {
  const { ask, graph: badGraph } = interactionGraph(
    () => ({ value: Number.NaN }),
    (state) => state,
  );
  await run(
    badGraph,
    { value: 0 },
    {
      interactions: interactions().handle(ask, () => ({ value: 0 })),
    },
  );
});

await capture("interactionResponse", async () => {
  const { ask, graph: badGraph } = interactionGraph(
    (state) => state,
    (state) => state,
  );
  await run(
    badGraph,
    { value: 0 },
    {
      interactions: interactions().handle(ask, () => ({ value: Number.NaN })),
    },
  );
});

await capture("interactionAppliedState", async () => {
  const { ask, graph: badGraph } = interactionGraph(
    (state) => state,
    () => ({ value: Number.NaN }),
  );
  await run(
    badGraph,
    { value: 0 },
    {
      interactions: interactions().handle(ask, ({ value }) => ({ value })),
    },
  );
});

console.log(JSON.stringify(results));
process.exit(0);
