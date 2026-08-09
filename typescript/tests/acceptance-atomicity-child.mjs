import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { z } from "zod";
import { agent, capability, output, pipeline, route, run } from "../packages/sdk/dist/index.js";

const mode = process.argv[2];
const directory = mkdtempSync(join(tmpdir(), "tandem-atomicity-"));
const logPath = join(directory, "requests.jsonl");
const ledgerPath = join(directory, "ledger.sqlite3");
const serverFile =
  mode === "capability" ? "function-protocol-server.mjs" : "openai-server-child.mjs";
const server = spawn(process.execPath, [new URL(serverFile, import.meta.url).pathname, logPath], {
  stdio: ["ignore", "pipe", "inherit"],
});
const port = await new Promise((resolve, reject) => {
  server.once("error", reject);
  server.stdout.once("data", (data) => resolve(Number(data.toString().trim())));
});

const State = z.object({ value: z.number() });
let applyCalled = false;
const submit = capability({
  name: "submit_implementation",
  schema: z.object({ implementation: z.string(), rationale: z.string() }),
  apply: () => {
    applyCalled = true;
    throw new Error("apply failed after durable acceptance");
  },
  summarize: ({ rationale }) => rationale,
});
const worker = agent({
  id: "worker",
  instructions: "Return a value.",
  client: {
    kind: "openai-compatible",
    version: 1,
    endpoint: `http://127.0.0.1:${port}/v1`,
    model: mode === "capability" ? "fixture-ds4" : "gpt-5.6-sol",
    wireApi: mode === "capability" ? "completions" : "responses",
  },
  message: () => "work",
  capabilities: mode === "capability" ? [submit] : [],
  output:
    mode === "output"
      ? {
          schema: z.object({ answer: z.number() }),
          apply: () => {
            applyCalled = true;
            throw new Error("apply failed after durable acceptance");
          },
        }
      : undefined,
  persist: true,
});
const done = output({ id: "done", summary: () => "done" });
const failed = output({ id: "failed", failed: true, summary: () => "failed" });
const graph = pipeline({
  name: `atomic-${mode}`,
  state: State,
  nodes: [worker, done, failed],
  start: worker,
  routes: [
    route({ from: worker, to: done, outcome: "success", label: "done" }),
    route({ from: worker, to: failed, outcome: "failed", label: "failed" }),
  ],
  outputs: [done, failed],
  persist: true,
});

try {
  let error = null;
  let succeeded = null;
  try {
    succeeded = (await run(graph, { value: 0 }, { ledgerPath })).succeeded;
  } catch (caught) {
    error = String(caught);
  }
  const db = new DatabaseSync(ledgerPath, { readOnly: true });
  const entries = db.prepare("select payload from run_entries order by sequence").all();
  db.close();
  const records = entries.map(({ payload }) => JSON.parse(Buffer.from(payload).toString("utf8")));
  console.log(
    JSON.stringify({
      error,
      succeeded,
      applyCalled,
      persistedAcceptance: records.some(({ kind }) => kind === (mode === "capability" ? 13 : 12)),
    }),
  );
} finally {
  server.kill();
  if (!existsSync(logPath) || readFileSync(logPath, "utf8").length === 0) {
    process.exitCode = 1;
  }
  rmSync(directory, { recursive: true, force: true });
  process.exit(process.exitCode ?? 0);
}
