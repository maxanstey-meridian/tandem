import { type ChatClient } from "@tandem/sdk";
import { closeCli, runCli } from "@tandem/sdk/cli";
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

const args = process.argv.slice(2);
const initialState: State = {
  brief:
    (args[0] === "--" ? args.slice(1) : args).join(" ") ||
    "Write a hopeful song about finding your way home.",
  lyrics: null,
  lintFeedback: null,
  proofreaderFeedback: null,
  revision: 0,
  proofreaderAccepted: null,
};

if (!process.env.OPENROUTER_API_KEY) {
  process.stderr.write("OPENROUTER_API_KEY is required to run the Songwriter example.\n");
  closeCli(2);
}
await runCli(
  createPipeline({ songwriter: openRouterDs4Client, proofreader: localSolClient }),
  initialState,
  {
    signal: AbortSignal.timeout(600_000),
    formatResult: (result) => `Lyrics:\n${result.state.lyrics ?? ""}`,
  },
);
