import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { z } from "zod";

const directory = mkdtempSync(join(tmpdir(), "tandem-capability-fixture-"));
const logPath = join(directory, "requests.jsonl");
const server = spawn(
  process.execPath,
  [new URL("openai-server-child.mjs", import.meta.url).pathname, logPath],
  { stdio: ["ignore", "pipe", "inherit"] },
);
const port = await new Promise((resolve, reject) => {
  server.once("error", reject);
  server.stdout.once("data", (data) => resolve(Number(data.toString().trim())));
});
const { agent, capability, output, pipeline, route, run } =
  await import("../packages/sdk/dist/index.js");

const State = z.object({ prompt: z.string(), accepted: z.boolean() });
const accept = capability({
  name: "accept",
  schema: z.object({ accepted: z.boolean() }),
  apply: (state, request) => ({ ...state, accepted: request.accepted }),
  summarize: () => "accepted",
});
const reject = capability({
  name: "reject",
  schema: z.object({ reason: z.string() }),
  apply: (state) => ({ ...state, accepted: false }),
  summarize: (request) => request.reason,
});
const executor = agent({
  id: "executor",
  instructions: "Use a declared capability or return structured output.",
  client: {
    kind: "openai-compatible",
    version: 1,
    endpoint: `http://127.0.0.1:${port}/v1`,
    model: "fixture",
    wireApi: "completions",
  },
  message: (state) => `CAPABILITY STATE MESSAGE: ${state.prompt}`,
  capabilities: [accept, reject],
  output: {
    schema: z.object({ accepted: z.boolean() }),
    apply: (state, value) => ({ ...state, accepted: value.accepted }),
  },
  continueSession: true,
  timeoutMs: 5000,
});
const done = output({ id: "done", summary: () => "done" });
const graph = pipeline({
  name: "capability-message",
  state: State,
  nodes: [executor, done],
  start: executor,
  routes: [route({ from: executor, to: done, outcome: "success", label: "accepted" })],
  outputs: [done],
});

try {
  let accepted = null;
  let error = null;
  try {
    accepted = (await run(graph, { prompt: "from-typescript-capability-state", accepted: false }))
      .state.accepted;
  } catch (caught) {
    error = String(caught);
  }
  const requests = readFileSync(logPath, "utf8").trim().split("\n").map(JSON.parse);
  console.log(
    JSON.stringify({
      accepted,
      error,
      body: requests.find((item) => item.url === "/v1/chat/completions")?.body,
    }),
  );
  server.kill();
  rmSync(directory, { recursive: true, force: true });
  process.exit(0);
} catch (error) {
  console.error(error);
  server.kill();
  rmSync(directory, { recursive: true, force: true });
  process.exit(1);
}
