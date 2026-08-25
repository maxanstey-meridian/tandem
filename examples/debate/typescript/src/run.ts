import { type ChatClient } from "@maxanstey-meridian/tandem";
import { closeCli, runCli } from "@maxanstey-meridian/tandem/cli";
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
  verifyModel: true,
} as const satisfies ChatClient;

const args = process.argv.slice(2);
const question =
  (args[0] === "--" ? args.slice(1) : args).join(" ") ||
  "Should cities replace most downtown parking with public space?";

const initialState: State = {
  question,
  arguments: [],
  round: 0,
  verdict: null,
  critiqueAccepted: null,
};

if (!process.env.OPENROUTER_API_KEY) {
  process.stderr.write("OPENROUTER_API_KEY is required to run the Debate example.\n");
  closeCli(2);
}
await runCli(
  createPipeline({
    proposer: openRouterDs4Client,
    critic: localSolClient,
    judge: localSolClient,
  }),
  initialState,
  {
    signal: AbortSignal.timeout(600_000),
    formatResult: (result) => `Verdict: ${JSON.stringify(result.state.verdict)}`,
  },
);
