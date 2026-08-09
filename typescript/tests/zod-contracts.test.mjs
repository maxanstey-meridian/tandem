import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { test } from "node:test";
import { promisify } from "node:util";

const exec = promisify(execFile);

test("Zod contracts reject unsupported behavior and schemas with contract errors", async () => {
  const { stdout } = await exec(
    process.execPath,
    [new URL("zod-contracts-child.mjs", import.meta.url).pathname],
    { timeout: 15_000 },
  );
  const errors = JSON.parse(stdout.trim());
  assert.equal(errors.length, 6);
  assert.match(errors[0], /changed the boundary value/);
  assert.match(errors[1], /Async Zod refinements/);
  assert.match(errors[2], /changed the boundary value/);
  assert.match(errors[3], /duplicate capability/);
  for (const encoded of errors.slice(4)) {
    const error = JSON.parse(encoded);
    assert.equal(error.name, "ContractValidationError");
    assert.equal(error.contract, true);
    assert.equal(error.problems[0].path, "$");
    assert.match(error.problems[0].message, /representable|Custom/i);
  }
});
