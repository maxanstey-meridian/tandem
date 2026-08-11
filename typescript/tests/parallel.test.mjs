import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { test } from "node:test";
import { promisify } from "node:util";

const exec = promisify(execFile);
const runChild = async (file, ...args) => {
  const { stdout } = await exec(
    process.execPath,
    [new URL(file, import.meta.url).pathname, ...args],
    { timeout: 15_000 },
  );
  return JSON.parse(stdout.trim());
};

test("runs parallel branches concurrently and merges by authored key", async () => {
  const result = await runChild("parallel-child.mjs");
  assert.equal(result.entered, 2);
  assert.equal(result.mergeCount, 1);
  assert.deepEqual(result.values, ["first", "second"]);
});

test("parallel caller cancellation aborts both active branch callbacks", async () => {
  const result = await runChild("parallel-outcomes-child.mjs", "cancel");
  assert.equal(result.entered, 2);
  assert.equal(result.abortedBranches, 2);
  assert.match(result.error, /AbortError/);
});

test("parallel rejects invalid merged state", async () => {
  const result = await runChild("parallel-outcomes-child.mjs", "invalid-merge");
  assert.equal(result.name, "ContractValidationError");
  assert.match(result.error, /merged state validation failed/);
});

test("parallel callback failures fault the run", async () => {
  const result = await runChild("parallel-outcomes-child.mjs", "callback-failure");
  assert.match(result.error, /parallel callback failed/);
});

test("parallel persists branch and merged accepted values", async () => {
  const result = await runChild("parallel-outcomes-child.mjs", "persist");
  assert.deepEqual(result.acceptedSteps, ["concurrent", "first", "second"]);
});

test("parallel isolates concurrent runs of one graph", async () => {
  const result = await runChild("parallel-outcomes-child.mjs", "concurrent");
  assert.deepEqual(result.values[0], ["alpha-first", "alpha-second"]);
  assert.deepEqual(result.values[1], ["beta-first", "beta-second"]);
});

test("parallel executes agent and stage branches through the packaged bridge", async () => {
  const result = await runChild("parallel-agent-child.mjs");
  assert.deepEqual(result.values, ["agent:42", "stage"]);
  assert.equal(result.modelBody.reasoning.effort, "none");
  assert.equal(result.modelBody.temperature, 0);
  assert.equal(result.modelBody.max_output_tokens, 2048);
});
