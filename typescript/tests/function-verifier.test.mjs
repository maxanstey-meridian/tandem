import assert from "node:assert/strict";
import { test } from "node:test";
import { verifyFunctionSource } from "../sample/src/support/verify-function-source.ts";

const valid = `(input) => input.trim().toLowerCase().normalize("NFD").replace(/[\\u0300-\\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "")`;

test("function verifier accepts a valid slugify expression", async () => {
  const result = await verifyFunctionSource(valid);
  assert.equal(result.passed, true);
  assert.equal(result.error, null);
  assert.equal(
    result.cases.every(({ passed }) => passed),
    true,
  );
});

for (const [name, source, error] of [
  ["syntax errors", "(input) => {", /Unexpected|Invalid/],
  ["infinite loops", "function (input) { while (true) {} }", /timed out/],
  ["Promise returns", "(input) => Promise.resolve(input)", /Promise|not defined/],
  ["non-string returns", "(input) => 42", /string is required/],
  [
    "process and filesystem access",
    "(input) => process.mainModule.require('node:fs').readFileSync(input, 'utf8')",
    /process is not defined/,
  ],
]) {
  test(`function verifier rejects ${name}`, async () => {
    const result = await verifyFunctionSource(source);
    assert.equal(result.passed, false);
    assert.match(JSON.stringify(result), error);
  });
}
