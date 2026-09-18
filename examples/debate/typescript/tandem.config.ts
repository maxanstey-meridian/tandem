import { type ChatClient } from "@maxanstey-meridian/tandem";
import { createPipeline } from "./src/pipeline.js";

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

export const tandem = {
  createPipeline: () =>
    createPipeline({
      proposer: openRouterDs4Client,
      critic: localSolClient,
      judge: localSolClient,
    }),
};
