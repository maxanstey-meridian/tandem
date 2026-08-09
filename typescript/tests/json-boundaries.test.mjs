import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { test } from "node:test";
import { promisify } from "node:util";

const exec = promisify(execFile);

test("rejects values that cannot cross JSON boundaries losslessly", async () => {
  const { stdout } = await exec(
    process.execPath,
    [new URL("json-boundaries-child.mjs", import.meta.url).pathname],
    { timeout: 15_000 },
  );
  const results = JSON.parse(stdout.trim());
  for (const name of [
    "nan",
    "infinity",
    "bigint",
    "undefinedProperty",
    "undefinedArrayEntry",
    "sparse",
    "arrayWithHiddenState",
    "date",
    "instance",
    "symbolProperty",
    "hidden",
    "toJSON",
    "cyclic",
  ]) {
    assert.equal(results[name].name, "ContractValidationError", name);
    assert.equal(results[name].contract, true, name);
    assert.equal(results[name].boundary, "initial state", name);
    assert(results[name].problem.message.length > 0, name);
  }
  assert.equal(results.stageOutput.boundary, "bad-stage output");
  assert.equal(results.interactionRequest.boundary, "ask request");
  assert.equal(results.interactionResponse.boundary, "ask response");
  assert.equal(results.interactionAppliedState.boundary, "ask applied state");
});
