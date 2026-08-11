const { agent } = await import("../packages/sdk/dist/index.js");

const client = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "http://127.0.0.1:10531/v1",
  model: "fixture",
  wireApi: "completions",
};
const definition = {
  id: "invalid",
  instructions: "Work.",
  client,
  message: () => "work",
};
const capture = (create) => {
  try {
    create();
    return null;
  } catch (error) {
    return String(error);
  }
};

console.log(
  JSON.stringify([
    capture(() => agent({ ...definition, client: { ...client, reasoningEffort: "disabled" } })),
    capture(() => agent({ ...definition, temperature: Number.NaN })),
    capture(() => agent({ ...definition, temperature: Number.POSITIVE_INFINITY })),
    capture(() => agent({ ...definition, temperature: -0.1 })),
    capture(() => agent({ ...definition, temperature: 2.1 })),
    capture(() => agent({ ...definition, maxOutputTokens: 0 })),
    capture(() => agent({ ...definition, maxOutputTokens: 1.5 })),
    capture(() => agent({ ...definition, maxOutputTokens: 2_147_483_648 })),
  ]),
);
process.exit(0);
