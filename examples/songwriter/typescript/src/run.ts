import { run, type ChatClient } from "@tandem/sdk";
import { closeCli } from "@tandem/sdk/cli";
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
  brief: process.argv.slice(2).join(" ") || "Write a hopeful song about finding your way home.",
  lyrics: null,
  lintFeedback: null,
  proofreaderFeedback: null,
  revision: 0,
  proofreaderAccepted: null,
};

let exitCode = 1;
try {
  if (!process.env.OPENROUTER_API_KEY) {
    throw new Error("OPENROUTER_API_KEY is required to run the Songwriter example.");
  }
  const result = await run(
    createPipeline({ songwriter: openRouterDs4Client, proofreader: localSolClient }),
    initialState,
    { signal: AbortSignal.timeout(180_000) },
  );
  console.log(JSON.stringify(result, null, 2));
  exitCode = result.succeeded ? 0 : 1;
} catch (error) {
  console.error(error);
} finally {
  closeCli(exitCode);
}
