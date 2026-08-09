import { createPipeline as createDebate } from "../../examples/debate/typescript/src/pipeline.js";
import { createPipeline as createSongwriter } from "../../examples/songwriter/typescript/src/pipeline.js";
import type { ChatClient } from "../packages/sdk/src/index.js";

const client = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "http://localhost:10531/v1",
  model: "test",
  wireApi: "responses",
} as const satisfies ChatClient;

const shape = (graph: ReturnType<typeof createSongwriter> | ReturnType<typeof createDebate>) => ({
  nodes: graph.nodes.map((node) => node.id),
  routes: graph.routes.length,
});

console.log(
  JSON.stringify({
    songwriter: shape(createSongwriter({ songwriter: client, proofreader: client })),
    debate: shape(createDebate({ proposer: client, critic: client, judge: client })),
  }),
);
process.exit(0);
