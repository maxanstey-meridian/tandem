import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { test } from "node:test";
import { promisify } from "node:util";

const exec = promisify(execFile);
async function child(mode) {
  try {
    const result = await exec(
      process.execPath,
      [new URL("cli-child.mjs", import.meta.url).pathname, mode],
      { timeout: 15_000 },
    );
    return { ...result, code: 0 };
  } catch (error) {
    return { stdout: error.stdout, stderr: error.stderr, code: error.code };
  }
}

test("runCli requests terminal presentation, then formats success exactly once", async () => {
  const result = await child("success");
  assert.equal(result.code, 0);
  assert.equal(result.stderr, "");
  assert.equal(result.stdout.includes(`${String.fromCharCode(27)}[`), false);
  assert.match(result.stdout, /pipeline cli-success run [0-9a-f]+ started/);
  assert.match(result.stdout, /pipeline Succeeded: complete/);
  assert.equal(result.stdout.match(/FORMAT_MARKER/g)?.length, 1);
  assert(
    result.stdout.indexOf("pipeline Succeeded: complete") < result.stdout.indexOf("FORMAT_MARKER"),
  );
});

test("runCli maps declared failure to exit 1 and still formats once", async () => {
  const result = await child("declared-failure");
  assert.equal(result.code, 1);
  assert.equal(result.stderr, "");
  assert.equal(result.stdout.match(/FORMAT_MARKER/g)?.length, 1);
  assert.match(result.stdout, /pipeline Failed: declared failure/);
});

test("runCli maps faults and cancellation to exit 2 without semantic output", async () => {
  const fault = await child("fault");
  assert.equal(fault.code, 2);
  assert.match(fault.stderr, /fixture fault/);
  assert.doesNotMatch(fault.stdout, /FORMAT_MARKER/);
  assert.doesNotMatch(fault.stderr, /TandemRuntimeError|\n\s+at /);

  const cancellation = await child("cancel");
  assert.equal(cancellation.code, 2);
  assert.match(cancellation.stderr, /cancel|abort|timed out/i);
  assert.doesNotMatch(cancellation.stdout, /FORMAT_MARKER/);
});

test("runCli distinguishes post-success result formatting failure", async () => {
  const result = await child("format-fault");
  assert.equal(result.code, 2);
  assert.match(result.stdout, /pipeline Succeeded: complete/);
  assert.match(result.stderr, /Pipeline completed, but result output failed: formatter failed/);
  assert.doesNotMatch(result.stdout, /FORMAT_MARKER/);
});
