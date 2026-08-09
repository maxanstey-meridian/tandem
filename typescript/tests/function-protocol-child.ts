import { inspectAccepted, run, type ChatClient } from "@tandem/sdk";
import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createPipeline } from "../sample/src/pipeline.js";

const directory = mkdtempSync(join(tmpdir(), "tandem-function-protocol-"));
const logPath = join(directory, "requests.jsonl");
const ledgerPath = join(directory, "function.sqlite3");
const server = spawn(
  process.execPath,
  [new URL("function-protocol-server.mjs", import.meta.url).pathname, logPath],
  { stdio: ["ignore", "pipe", "inherit"] },
);
const port = await new Promise<number>((resolve, reject) => {
  server.once("error", reject);
  server.stdout.once("data", (data) => resolve(Number(data.toString().trim())));
});
const endpoint = `http://127.0.0.1:${port}/v1`;
const implementer: ChatClient = {
  kind: "openai-compatible",
  version: 1,
  endpoint,
  model: "fixture-ds4",
  wireApi: "completions",
};
const reviewer: ChatClient = {
  kind: "openai-compatible",
  version: 1,
  endpoint,
  model: "gpt-5.6-sol",
  wireApi: "responses",
  reasoningEffort: "low",
  verifyModel: true,
};

try {
  const result = await run(
    createPipeline({ implementer, reviewer }),
    {
      requirements: [
        "Implement synchronous pure JavaScript slugify(input).",
        "Trim whitespace, lowercase, remove Unicode diacritics, collapse non-alphanumeric runs to one hyphen, trim edge hyphens, and return empty when no alphanumeric remains.",
      ],
      implementation: null,
      verification: null,
      review: null,
    },
    { ledgerPath },
  );
  const accepted = await inspectAccepted({ ledgerPath, runId: result.runId });
  const requests = readFileSync(logPath, "utf8").trim().split("\n").map(JSON.parse);
  console.log(JSON.stringify({ result, accepted, requests }));
} catch (error) {
  console.error(readFileSync(logPath, "utf8"));
  throw error;
} finally {
  server.kill();
  rmSync(directory, { recursive: true, force: true });
}
process.exit(0);
