import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { test } from "node:test";
import { promisify } from "node:util";
const exec = promisify(execFile);

async function child(mode, timeout = 15_000) {
  const { stdout } = await exec(
    process.execPath,
    [new URL("lifecycle-child.mjs", import.meta.url).pathname, mode],
    { timeout },
  );
  return JSON.parse(stdout.trim());
}

test("runs package-relatively, persists accepted values, and terminalizes", async () => {
  const result = await child("single");
  assert.deepEqual(result.values, [1]);
  assert.deepEqual(result.statuses, ["Ready"]);
  assert.equal(result.terminalized, true);
  assert.equal(result.sqlite, true);
  assert(result.accepted > 0);
  assert(result.acceptedVersions.every((version) => version === 1));
  assert(
    result.acceptedKinds.every((kind) =>
      [
        "StructuredOutputAccepted",
        "CapabilityAccepted",
        "InteractionRequested",
        "InteractionAnswered",
        "StepCompleted",
      ].includes(kind),
    ),
  );
});

test("preserves structured initial-state validation problems", async () => {
  const result = await child("invalid");
  assert.deepEqual(result.problems, [
    { path: "$.count", message: "Invalid input: expected number, received string" },
  ]);
});

test("translates callback failures and cancellation", async () => {
  const faulted = await child("failure");
  assert.match(faulted.error, /callback exploded/);
  assert.equal(faulted.name, "TandemRuntimeError");
  assert.equal(faulted.operation, "run");
  assert.match(faulted.cause, /callback exploded/);
  assert.deepEqual(faulted.statuses, ["Faulted"]);
  assert.equal(faulted.terminalized, true);
  const cancelled = await child("cancel");
  assert.match(cancelled.error, /cancel/i);
  assert.equal(cancelled.name, "AbortError");
  assert.equal(cancelled.operation, "run");
  assert.match(cancelled.cause, /cancel/i);
  assert.deepEqual(cancelled.statuses, ["Cancelled"]);
  assert.equal(cancelled.terminalized, true);
  assert.equal(cancelled.abortObserved, true);
  assert.equal(cancelled.mutatedAfterAbort, false);
});

test("terminalizes declared pipeline failure", async () => {
  const result = await child("failed");
  assert.equal(result.succeeded, false);
  assert.deepEqual(result.statuses, ["Failed"]);
  assert.equal(result.terminalized, true);
});

test("rejects inspection of an unknown run", async () => {
  const result = await child("unknown-inspect");
  assert.match(result.error, /does not exist/);
  assert.equal(result.operation, "inspect");
});

test("executes a typed interaction through Tandem", async () => {
  assert.deepEqual(await child("interaction"), { count: 5, done: true });
  assert.deepEqual(await child("interaction-chain"), { count: 6, done: true });
});

test("cancels an active interaction through its AbortSignal without applying a response", async () => {
  const cancelled = await child("interaction-cancel");
  assert.equal(cancelled.name, "AbortError");
  assert.equal(cancelled.abortObserved, true);
  assert.equal(cancelled.handlerCompleted, false);
  assert.equal(cancelled.interactionApplied, false);
  assert.equal(cancelled.mutatedAfterAbort, false);
});

test("supports concurrent and repeated runs in one loaded host", async () => {
  assert.equal((await child("concurrent")).results, 8);
  assert.equal((await child("repeated")).results, 5);
});

test("rejects invalid participant identities and incomplete graph shapes", async () => {
  const { stdout } = await exec(
    process.execPath,
    [new URL("identity-child.mjs", import.meta.url).pathname],
    { timeout: 10_000 },
  );
  const errors = JSON.parse(stdout.trim());
  assert.equal(errors.length, 6);
  assert.match(errors[0], /start/);
  assert.match(errors[1], /Route/);
  assert.match(errors[2], /Output/);
  assert.match(errors[3], /both unconditional/);
  assert.match(errors[4], /must be listed in outputs/);
  assert.match(errors[5], /must be reachable/);
});

test("bounded soak completes and exits", async () => {
  const result = await child("soak", 30_000);
  assert.equal(result.results, 25);
  assert(result.statuses.every((status) => status === "Ready"));
});

test("planner preflight and model requests proceed through a local protocol fixture", async () => {
  const { stdout } = await exec(
    "/usr/bin/env",
    [
      "-u",
      "NODE_TEST_CONTEXT",
      process.execPath,
      new URL("planner-child.mjs", import.meta.url).pathname,
    ],
    { timeout: 15_000 },
  );
  const result = JSON.parse(stdout.trim());
  assert.equal(result.error, null);
  assert.equal(result.answer, 42);
  assert.equal(result.urls[0], "/v1/models");
  assert(result.urls.slice(1).every((url) => url === "/v1/responses"));
  assert(result.urls.length >= 2);
  assert.match(JSON.stringify(result.modelBody), /STATE MESSAGE: from-typescript-state/);
});

test("one agent composes its authored message, multiple capabilities, structured output, and policies", async () => {
  const { stdout } = await exec(
    "/usr/bin/env",
    [
      "-u",
      "NODE_TEST_CONTEXT",
      process.execPath,
      new URL("capability-message-child.mjs", import.meta.url).pathname,
    ],
    { timeout: 15_000 },
  );
  const result = JSON.parse(stdout.trim());
  assert.equal(result.error, null);
  assert.equal(result.accepted, true);
  assert.match(
    JSON.stringify(result.body),
    /CAPABILITY STATE MESSAGE: from-typescript-capability-state/,
  );
  assert.match(JSON.stringify(result.body), /accept/);
  assert.match(JSON.stringify(result.body), /reject/);
  assert.equal(result.body.tools.length, 2);
  assert.match(JSON.stringify(result.body.response_format), /json_schema/);
});

test("rolls back durable acceptance when JavaScript state application faults", async () => {
  for (const mode of ["capability", "output"]) {
    const { stdout } = await exec(
      process.execPath,
      [new URL("acceptance-atomicity-child.mjs", import.meta.url).pathname, mode],
      { timeout: 15_000 },
    );
    const result = JSON.parse(stdout.trim());
    assert.equal(result.applyCalled, true);
    assert.match(result.error, /apply failed after durable acceptance/);
    assert.equal(result.persistedAcceptance, false);
  }
});
