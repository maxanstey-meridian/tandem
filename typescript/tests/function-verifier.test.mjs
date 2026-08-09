import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { test } from "node:test";
import { promisify } from "node:util";

const valid = `(input) => input.trim().toLowerCase().normalize("NFD").replace(/[\\u0300-\\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "")`;
const sources = [
  valid,
  "(input) => {",
  "42",
  "(input) => { while (true) {} }",
  "(input) => process.exit(0)",
];
const exec = promisify(execFile);

test("function verifier accepts valid source and rejects invalid source", async () => {
  const { stdout } = await exec(
    "pnpm",
    ["exec", "tsx", new URL("function-verifier-child.ts", import.meta.url).pathname, ...sources],
    { timeout: 30_000 },
  );
  const [result, syntaxError, nonFunction, nonTerminating, exiting] = JSON.parse(stdout.trim());

  assert.equal(result.passed, true);
  assert.equal(result.error, null);
  assert.equal(
    result.cases.every(({ passed }) => passed),
    true,
  );
  assert.equal(syntaxError.passed, false);
  assert.match(JSON.stringify(syntaxError), /Invalid JavaScript/);
  assert.equal(nonFunction.passed, false);
  assert.match(JSON.stringify(nonFunction), /must evaluate to a function/);
  assert.equal(nonTerminating.passed, false);
  assert.match(nonTerminating.error, /timed out/);
  assert.equal(exiting.passed, false);
  assert.match(JSON.stringify(exiting), /process is not defined/);
});
