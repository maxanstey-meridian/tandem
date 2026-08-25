import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { test } from "node:test";

test("a long-running host can cancel Tandem and close before SIGTERM exit", async () => {
  const child = spawn(process.execPath, [
    new URL("host-signal-child.mjs", import.meta.url).pathname,
  ]);
  let stdout = "";
  let stderr = "";
  let signalSent = false;
  child.stdout.setEncoding("utf8");
  child.stderr.setEncoding("utf8");
  child.stdout.on("data", (chunk) => {
    stdout += chunk;
    if (!signalSent && stdout.includes("HOST_STARTED") && stdout.includes("RUN_STARTED")) {
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
      reject(
        new Error(
          `Long-running host did not exit after SIGTERM.\nstdout:\n${stdout}\nstderr:\n${stderr}`,
        ),
      );
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
  assert.equal(code, 143);
  assert.equal(stderr, "");
  assert.match(stdout, /HOST_CLEANED_UP/);
});
