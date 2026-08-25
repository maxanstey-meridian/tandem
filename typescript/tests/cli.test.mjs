import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
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

async function signalChild() {
  const child = spawn(process.execPath, [
    new URL("cli-child.mjs", import.meta.url).pathname,
    "signal",
  ]);
  let stdout = "";
  let stderr = "";
  let signalSent = false;
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => {
    stdout += chunk;
    if (!signalSent && stdout.includes("WORK_STARTED")) {
      signalSent = true;
      child.kill("SIGTERM");
    }
  });
  child.stderr.on("data", (chunk) => {
    stderr += chunk;
  });
  const code = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      child.kill("SIGKILL");
      reject(new Error("runCli did not exit after SIGTERM."));
    }, 10_000);
    child.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    child.once("exit", (exitCode) => {
      clearTimeout(timeout);
      resolve(exitCode);
    });
  });
  return { stdout, stderr, code };
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
  assert.match(fault.stderr, /\n\s+at /);

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
  assert.match(result.stderr, /formatter failed\n\s+at /);
  assert.match(result.stderr, /Caused by: formatter cause\n\s+at /);
  assert.doesNotMatch(result.stdout, /FORMAT_MARKER/);
});

test("runCli aborts active work and awaits cleanup before exiting on SIGTERM", async () => {
  const result = await signalChild();
  assert.equal(result.code, 143);
  assert.equal(result.stderr, "");
  assert.match(result.stdout, /WORK_STARTED/);
  assert.match(result.stdout, /CANCELLED_MARKER/);
});
