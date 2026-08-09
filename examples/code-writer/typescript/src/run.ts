import { inspectAccepted, run, type ChatClient } from "@tandem/sdk";
import { closeCli } from "@tandem/sdk/cli";
import { randomUUID } from "node:crypto";
import { createPipeline } from "./pipeline.js";
import type { State } from "./state.js";

const openRouterDs4Client = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "https://openrouter.ai/api/v1",
  model: "deepseek/deepseek-v4-flash-0731",
  wireApi: "completions",
  apiKeyEnvironmentVariable: "OPENROUTER_API_KEY",
} as const satisfies ChatClient;

const localSolClient = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "http://127.0.0.1:10531/v1",
  model: "gpt-5.6-sol",
  wireApi: "responses",
  reasoningEffort: "low",
  verifyModel: true,
} as const satisfies ChatClient;

const initialState: State = {
  requirements: [
    "Implement synchronous pure JavaScript slugify(input).",
    "Trim whitespace and lowercase the input.",
    "Remove Unicode diacritics.",
    "Replace runs of non-alphanumeric characters with one hyphen.",
    "Trim edge hyphens and never return repeated hyphens.",
    "Return an empty string when no alphanumeric characters remain.",
  ],
  implementation: null,
  verification: null,
  review: null,
};

let exitCode = 1;
try {
  if (!process.env.OPENROUTER_API_KEY) {
    throw new Error("OPENROUTER_API_KEY is required to run the Code Writer example.");
  }
  const ledgerPath = process.env.TANDEM_LEDGER_PATH ?? `code-writer-${randomUUID()}.sqlite3`;
  const result = await run(
    createPipeline({ implementer: openRouterDs4Client, reviewer: localSolClient }),
    initialState,
    { ledgerPath, signal: AbortSignal.timeout(180_000) },
  );
  const accepted = await inspectAccepted({ ledgerPath, runId: result.runId });
  console.log(JSON.stringify({ ...result, ledgerPath, accepted }, null, 2));
  exitCode = result.succeeded ? 0 : 1;
} catch (error) {
  console.error(error);
} finally {
  closeCli(exitCode);
}
