import { z } from "zod";
import {
  agent,
  capability,
  ContractValidationError,
  output,
  pipeline,
  route,
  run,
  stage,
} from "../packages/sdk/dist/index.js";

const errors = [];
const done = output({ id: "done", summary: () => "done" });
const pass = stage({ id: "pass", execute: (state) => state });
const normalized = pipeline({
  name: "normalized",
  state: z.object({ value: z.coerce.number() }),
  nodes: [pass, done],
  start: pass,
  routes: [route({ from: pass, to: done, label: "done" })],
  outputs: [done],
});
try {
  await run(normalized, { value: "1" });
} catch (error) {
  errors.push(String(error));
}

const transformedState = pipeline({
  name: "transformed-state",
  state: z.object({ value: z.string().transform((value) => value.toUpperCase()) }),
  nodes: [pass, done],
  start: pass,
  routes: [route({ from: pass, to: done, label: "done" })],
  outputs: [done],
});
try {
  await run(transformedState, { value: "lowercase" });
} catch (error) {
  errors.push(String(error));
}

const asyncState = z.object({ value: z.number() }).refine(async () => true);
const asynchronous = pipeline({
  name: "async",
  state: asyncState,
  nodes: [pass, done],
  start: pass,
  routes: [route({ from: pass, to: done, label: "done" })],
  outputs: [done],
});
try {
  await run(asynchronous, { value: 1 });
} catch (error) {
  errors.push(String(error));
}

const bad = stage({ id: "bad", execute: () => ({ value: 1, hidden: true }) });
const stripped = pipeline({
  name: "stripped",
  state: z.object({ value: z.number() }),
  nodes: [bad, done],
  start: bad,
  routes: [route({ from: bad, to: done, label: "done" })],
  outputs: [done],
});
try {
  await run(stripped, { value: 0 });
} catch (error) {
  errors.push(
    JSON.stringify({
      name: error.name,
      contract: error instanceof ContractValidationError,
      problems: error.problems,
    }),
  );
}

const same = capability({
  name: "same",
  instructions: "Keep state unchanged.",
  schema: z.object({ value: z.number() }),
  apply: (state) => state,
  summarize: () => "same",
});
try {
  agent({
    id: "agent",
    instructions: "Test.",
    client: {
      kind: "openai-compatible",
      version: 1,
      endpoint: "http://localhost/v1",
      model: "test",
      wireApi: "responses",
    },
    message: () => "message",
    capabilities: [same, same],
  });
} catch (error) {
  errors.push(String(error));
}

try {
  capability({
    name: "unsupported",
    instructions: "Use an unsupported schema.",
    schema: z.custom(),
    apply: (state) => state,
    summarize: () => "unsupported",
  });
} catch (error) {
  errors.push(
    JSON.stringify({
      name: error.name,
      contract: error instanceof ContractValidationError,
      problems: error.problems,
    }),
  );
}

try {
  capability({
    name: "transformed",
    instructions: "Use a transformed schema.",
    schema: z.object({ value: z.string().transform((value) => value.toUpperCase()) }),
    apply: (state) => state,
    summarize: () => "transformed",
  });
} catch (error) {
  errors.push(String(error));
}

const unsupportedOutputAgent = agent({
  id: "unsupported-output",
  instructions: "Test.",
  client: {
    kind: "openai-compatible",
    version: 1,
    endpoint: "http://localhost/v1",
    model: "test",
    wireApi: "responses",
  },
  message: () => "message",
  output: {
    instructions: "Return an unsupported value.",
    schema: z.custom(),
    apply: (state) => state,
  },
});
const unsupportedOutput = pipeline({
  name: "unsupported-output",
  state: z.object({ value: z.number() }),
  nodes: [unsupportedOutputAgent, done],
  start: unsupportedOutputAgent,
  routes: [route({ from: unsupportedOutputAgent, to: done, label: "done", outcome: "success" })],
  outputs: [done],
});
try {
  await run(unsupportedOutput, { value: 1 });
} catch (error) {
  errors.push(
    JSON.stringify({
      name: error.name,
      contract: error instanceof ContractValidationError,
      problems: error.problems,
    }),
  );
}

for (const create of [
  () =>
    capability({
      name: "blank",
      instructions: " ",
      schema: z.object({}),
      apply: (state) => state,
      summarize: () => "blank",
    }),
  () =>
    agent({
      id: "blank-agent",
      instructions: " ",
      client: {
        kind: "openai-compatible",
        version: 1,
        endpoint: "http://localhost/v1",
        model: "test",
        wireApi: "responses",
      },
      message: () => "message",
    }),
  () =>
    agent({
      id: "blank-output",
      instructions: "Test.",
      client: {
        kind: "openai-compatible",
        version: 1,
        endpoint: "http://localhost/v1",
        model: "test",
        wireApi: "responses",
      },
      message: () => "message",
      output: {
        instructions: " ",
        schema: z.object({}),
        apply: (state) => state,
      },
    }),
]) {
  try {
    create();
  } catch (error) {
    errors.push(String(error));
  }
}

for (const create of [
  () =>
    capability({
      name: "missing-instructions",
      schema: z.object({}),
      apply: (state) => state,
      summarize: () => "missing",
    }),
  () =>
    agent({
      id: "non-string-instructions",
      instructions: 42,
      client: {
        kind: "openai-compatible",
        version: 1,
        endpoint: "http://localhost/v1",
        model: "test",
        wireApi: "responses",
      },
      message: () => "message",
    }),
]) {
  try {
    create();
  } catch (error) {
    errors.push(
      JSON.stringify({
        name: error.name,
        contract: error instanceof ContractValidationError,
        problems: error.problems,
      }),
    );
  }
}

console.log(JSON.stringify(errors));
process.exit(0);
