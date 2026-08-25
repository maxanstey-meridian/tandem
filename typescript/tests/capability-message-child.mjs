import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { z } from "zod";

const directory = mkdtempSync(join(tmpdir(), "tandem-capability-fixture-"));
const terminalPresentation = process.argv[2] === "terminal";
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
let contextualValidations = 0;
let applications = 0;
const accept = capability({
  name: "accept",
  instructions: "Accept the request.",
  schema: z.object({ accepted: z.boolean() }),
  validateFor: () => {
    contextualValidations += 1;
    return contextualValidations === 1
      ? [{ path: "$.accepted", message: "Confirm acceptance once." }]
      : [];
  },
  apply: (state, request) => {
    applications += 1;
    return { ...state, accepted: request.accepted };
  },
  summarize: () => "accepted",
});
const reject = capability({
  name: "reject",
  instructions: "Reject the request with a reason.",
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
    reasoningEffort: "none",
  },
  message: (state) => `CAPABILITY STATE MESSAGE: ${state.prompt}`,
  temperature: 0,
  maxOutputTokens: 1024,
  capabilities: [accept, reject],
  output: {
    instructions: "Return whether the request was accepted.",
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
    accepted = (
      await run(
        graph,
        { prompt: "from-typescript-capability-state", accepted: false },
        {
          presentation: terminalPresentation ? "terminal" : undefined,
          terminal: terminalPresentation ? { truncatedToolNames: ["accept"] } : undefined,
        },
      )
    ).state.accepted;
  } catch (caught) {
    error = String(caught);
  }
  const requests = readFileSync(logPath, "utf8").trim().split("\n").map(JSON.parse);
  console.log(
    JSON.stringify({
      accepted,
      error,
      body: requests.find((item) => item.url === "/v1/chat/completions")?.body,
      contextualValidations,
      applications,
      bodies: requests
        .filter((item) => item.url === "/v1/chat/completions")
        .map((item) => item.body),
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
