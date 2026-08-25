import assert from "node:assert/strict";
import { registerHooks } from "node:module";
import { test } from "node:test";
import { z } from "zod";

const runtimeUrl = "tandem:test-runtime";
registerHooks({
  resolve(specifier, context, nextResolve) {
    return specifier === "@tandem/runtime"
      ? { url: runtimeUrl, shortCircuit: true }
      : nextResolve(specifier, context);
  },
  load(url, context, nextLoad) {
    return url === runtimeUrl
      ? {
          format: "module",
          shortCircuit: true,
          source: `
            export async function runRegisteredGraphAsync(registration) {
              globalThis.__tandemRegistrations.push(JSON.parse(registration));
              const graph = JSON.parse(registration);
              return JSON.stringify({
                runId: "00000000-0000-4000-8000-000000000000",
                succeeded: true,
                state: JSON.parse(graph.initialState),
                summary: null,
              });
            }
            export async function inspectAcceptedAsync() {
              return "[]";
            }
          `,
        }
      : nextLoad(url, context);
  },
});

globalThis.__tandemRegistrations = [];
const { agent, capability, output, pipeline, route, run } =
  await import("../packages/sdk/dist/index.js");

const state = z.object({ value: z.number() });
const checkpointCapability = capability({
  name: "checkpoint",
  instructions: "Record a checkpoint.",
  schema: z.object({}),
  apply: (current) => current,
  summarize: () => "Checkpointed.",
});
const client = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "http://127.0.0.1:10531/v1",
  model: "test",
  wireApi: "responses",
};

function registration(disableCompaction) {
  const worker = agent({
    id: "worker",
    instructions: "Work.",
    client,
    message: () => "Work.",
    capabilities: [checkpointCapability],
    checkpoint: {
      contextWindowTokens: 100,
      maxOutputTokens: 20,
      checkpointAtPercent: 80,
      capability: checkpointCapability,
      instructions: "Checkpoint.",
      message: () => "Checkpoint now.",
      ...(disableCompaction === undefined ? {} : { disableCompaction }),
    },
  });
  const done = output({ id: "done", summary: () => "Done." });
  const graph = pipeline({
    name: "checkpoint-registration",
    state,
    nodes: [worker, done],
    start: worker,
    routes: [route({ from: worker, to: done, label: "done", outcome: "success" })],
    outputs: [done],
  });
  return run(graph, { value: 1 }).then(
    () => globalThis.__tandemRegistrations.at(-1).nodes[0].checkpoint,
  );
}

test("checkpoint registration serializes omitted disableCompaction as false", async () => {
  assert.equal((await registration(undefined)).disableCompaction, false);
});

test("checkpoint registration preserves explicit disableCompaction true", async () => {
  assert.equal((await registration(true)).disableCompaction, true);
});
