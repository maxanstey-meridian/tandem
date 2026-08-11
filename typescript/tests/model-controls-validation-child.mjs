import { agent } from "../packages/sdk/dist/index.js";

const base = {
  id: "worker",
  instructions: "Work.",
  client: {
    kind: "openai-compatible",
    version: 1,
    endpoint: "http://localhost:10531/v1",
    model: "test",
    wireApi: "responses",
  },
  message: () => "work",
};
const definitions = [
  { ...base, client: { ...base.client, reasoningEffort: "minimal" } },
  { ...base, temperature: -0.1 },
  { ...base, temperature: 2.1 },
  { ...base, temperature: Number.NaN },
  { ...base, maxOutputTokens: 0 },
  { ...base, maxOutputTokens: 1.5 },
  { ...base, maxOutputTokens: Number.MAX_SAFE_INTEGER + 1 },
];
const errors = definitions.map((definition) => {
  try {
    agent(definition);
    return null;
  } catch (error) {
    return { name: error.name, message: error.message };
  }
});

console.log(JSON.stringify(errors));
process.exit(0);
