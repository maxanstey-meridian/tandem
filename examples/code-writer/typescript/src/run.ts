import { inspectAccepted, type ChatClient } from "@maxanstey-meridian/tandem";
import { closeCli, runCli } from "@maxanstey-meridian/tandem/cli";
import { resolve } from "node:path";
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

if (!process.env.OPENROUTER_API_KEY) {
  process.stderr.write("OPENROUTER_API_KEY is required to run the Code Writer example.\n");
  closeCli(2);
}
const ledgerPath = resolve(process.env.TANDEM_LEDGER_PATH ?? "code-writer.sqlite3");
await runCli(
  createPipeline({ implementer: openRouterDs4Client, reviewer: localSolClient }),
  initialState,
  {
    ledgerPath,
    signal: AbortSignal.timeout(600_000),
    formatResult: async (result) => {
      const accepted = await inspectAccepted({ ledgerPath, runId: result.runId });
      return [
        `Implementation:\n${result.state.implementation?.source ?? ""}`,
        `Ledger: ${ledgerPath}`,
        `Run: ${result.runId}`,
        `Accepted: ${accepted.length}`,
      ].join("\n");
    },
  },
);
