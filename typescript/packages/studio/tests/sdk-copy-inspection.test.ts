import { inspectPipeline } from "@maxanstey-meridian/tandem";
import assert from "node:assert/strict";
import { copyFile, mkdtemp, rm } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { pathToFileURL, fileURLToPath } from "node:url";
import { z } from "zod";

test("inspection preserves every semantic kind across a separate physical SDK module copy", async () => {
  const source = fileURLToPath(new URL("../../sdk/dist/index.js", import.meta.url));
  const directory = await mkdtemp(path.join(path.dirname(source), "copy-"));
  const copy = path.join(directory, "index.mjs");
  await copyFile(source, copy);
  try {
    const sdk: any = await import(pathToFileURL(copy).href);
    const stage = sdk.stage({ id: "stage", execute: (state: unknown) => state });
    const interaction = sdk.interaction({
      id: "interaction",
      requestSchema: z.object({}),
      responseSchema: z.object({}),
      request: () => ({}),
      apply: (state: unknown) => state,
    });
    const agent = sdk.agent({
      id: "agent",
      instructions: "Act without executing during inspection.",
      client: {
        kind: "openai-compatible",
        version: 1,
        endpoint: "http://unused",
        model: "unused",
        wireApi: "responses",
      },
      message: () => "unused",
    });
    const parallel = sdk.parallel({
      id: "parallel",
      branches: {
        first: sdk.stage({ id: "first", execute: (state: unknown) => state }),
        second: sdk.stage({ id: "second", execute: (state: unknown) => state }),
      },
      merge: (states: unknown[]) => states[0],
    });
    const complete = sdk.output({ id: "complete", summary: () => "done" });
    const failure = sdk.output({ id: "failure", failed: true, summary: () => "failed" });
    const routes = [
      sdk.route({ from: stage, to: interaction, label: "interact" }),
      sdk.route({ from: interaction, to: agent, label: "delegate" }),
      sdk.route({ from: agent, to: parallel, label: "continue", outcome: "success" }),
      sdk.route({ from: agent, to: failure, label: "agent failed", outcome: "failed" }),
      sdk.route({ from: parallel, to: complete, label: "finish", outcome: "success" }),
      sdk.route({ from: parallel, to: failure, label: "parallel failed", outcome: "failed" }),
    ];
    const graph = sdk.pipeline({
      name: "copy-boundary",
      state: z.object({}),
      nodes: [stage, interaction, agent, parallel, complete, failure],
      routes,
      start: stage,
      outputs: [complete, failure],
    });
    assert.deepEqual(
      inspectPipeline(graph).nodes.map((node) => node.kind),
      ["stage", "interaction", "agent", "parallel", "completion", "failure"],
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
