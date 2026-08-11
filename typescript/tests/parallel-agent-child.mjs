import { spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { z } from "zod";

const directory = mkdtempSync(join(tmpdir(), "tandem-parallel-agent-"));
const logPath = join(directory, "requests.jsonl");
const server = spawn(
  process.execPath,
  [new URL("openai-server-child.mjs", import.meta.url).pathname, logPath],
  { stdio: ["ignore", "pipe", "inherit"] },
);
let cleanupPromise;
const cleanup = () =>
  (cleanupPromise ??= (async () => {
    if (server.exitCode === null && server.signalCode === null) {
      const exited = new Promise((resolve) => server.once("exit", resolve));
      server.kill();
      await exited;
    }
    rmSync(directory, { recursive: true, force: true });
  })());
process.once("SIGTERM", () => {
  void cleanup().finally(() => process.exit(143));
});
process.once("SIGINT", () => {
  void cleanup().finally(() => process.exit(130));
});
const port = await new Promise((resolve, reject) => {
  server.once("error", reject);
  server.stdout.once("data", (data) => resolve(Number(data.toString().trim())));
});
const { agent, output, parallel, pipeline, route, run, stage } =
  await import("../packages/sdk/dist/index.js");

const State = z.object({ values: z.array(z.string()) });
const worker = agent({
  id: "worker",
  instructions: "Return the numeric answer.",
  client: {
    kind: "openai-compatible",
    version: 1,
    endpoint: `http://127.0.0.1:${port}/v1`,
    model: "gpt-5.6-sol",
    wireApi: "responses",
  },
  message: () => "Return the answer.",
  output: {
    instructions: "Return the numeric answer.",
    schema: z.object({ answer: z.number() }),
    apply: (state, value) => ({ values: [...state.values, `agent:${value.answer}`] }),
  },
});
const local = stage({
  id: "local",
  execute: (state) => ({ values: [...state.values, "stage"] }),
});
const concurrent = parallel({
  id: "concurrent",
  branches: { worker, local },
  merge: (baseline, results) => ({
    values: [...baseline.values, ...results.worker.values, ...results.local.values],
  }),
});
const done = output({ id: "done", summary: (state) => state.values.join(",") });
const graph = pipeline({
  name: "parallel-agent",
  state: State,
  nodes: [concurrent, done],
  start: concurrent,
  routes: [route({ from: concurrent, outcome: "success", to: done, label: "done" })],
  outputs: [done],
});

try {
  const result = await run(graph, { values: [] });
  console.log(JSON.stringify({ values: result.state.values }));
} finally {
  await cleanup();
}
process.exit(0);
