import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { z } from "zod";

const directory = mkdtempSync(join(tmpdir(), "tandem-openai-fixture-"));
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
const { agent, output, pipeline, route, run } = await import("../packages/sdk/dist/index.js");

const State = z.object({ prompt: z.string(), answer: z.number().nullable() });
const planner = agent({
  id: "planner",
  instructions: "Return a structured answer.",
  client: {
    kind: "openai-compatible",
    version: 1,
    endpoint: `http://127.0.0.1:${port}/v1`,
    model: "gpt-5.6-sol",
    wireApi: "responses",
    reasoningEffort: "low",
    verifyModel: true,
  },
  message: (state) => `STATE MESSAGE: ${state.prompt}`,
  output: {
    schema: z.object({ answer: z.number() }),
    apply: (state, value) => ({ ...state, answer: value.answer }),
  },
});
const done = output({ id: "done", summary: (state) => String(state.answer) });
const graph = pipeline({
  name: "planner-fixture",
  state: State,
  nodes: [planner, done],
  start: planner,
  routes: [route({ from: planner, to: done, outcome: "success", label: "accepted" })],
  outputs: [done],
});

try {
  let answer = null;
  let error = null;
  try {
    answer = (await run(graph, { prompt: "from-typescript-state", answer: null })).state.answer;
  } catch (caught) {
    error = String(caught);
  }
  const requests = readFileSync(logPath, "utf8").trim().split("\n").map(JSON.parse);
  const modelRequests = requests.filter((item) => item.url === "/v1/responses");
  console.log(
    JSON.stringify({
      answer,
      error,
      urls: requests.map((item) => item.url),
      modelBody: modelRequests[0]?.body,
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
