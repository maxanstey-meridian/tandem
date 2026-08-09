import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { test } from "node:test";
import { promisify } from "node:util";
import { assessImplementation } from "../sample/src/infrastructure/assess-implementation.ts";

const exec = promisify(execFile);

test("function implementation loops through verification and review to accepted code", async () => {
  const { stdout } = await exec(
    "/usr/bin/env",
    [
      "-u",
      "NODE_TEST_CONTEXT",
      "pnpm",
      "exec",
      "tsx",
      new URL("function-protocol-child.ts", import.meta.url).pathname,
    ],
    { timeout: 30_000 },
  );
  const { result, accepted, requests } = JSON.parse(stdout.trim());
  const modelRequests = requests.filter(({ url }) => url !== "/v1/models");

  assert.deepEqual(
    modelRequests.map(({ url }) => url),
    [
      "/v1/chat/completions",
      "/v1/chat/completions",
      "/v1/responses",
      "/v1/chat/completions",
      "/v1/responses",
    ],
  );
  assert.match(JSON.stringify(modelRequests[0].body), /role.*system.*role.*user/s);
  assert.match(JSON.stringify(modelRequests[1].body), /Verification feedback to address/s);
  assert.match(JSON.stringify(modelRequests[1].body), /cr-me-br-l-e/);
  assert.match(
    JSON.stringify(modelRequests[2].body),
    /Exact source.*normalize.*Passing verification evidence/s,
  );
  assert.match(JSON.stringify(modelRequests[2].body), /\\"passed\\":true/);
  assert.match(
    JSON.stringify(modelRequests[3].body),
    /Reviewer feedback to address.*Use a named function expression/s,
  );
  assert.equal(result.succeeded, true);
  assert.equal(result.summary, "The slugify implementation is accepted.");
  assert.equal(result.state.verification.passed, true);
  assert.deepEqual(result.state.review, {
    decision: "Accept",
    summary: "The slugify implementation is accepted.",
    findings: [],
  });
  assert.equal((await assessImplementation(result.state.implementation.source)).passed, true);

  assert.deepEqual(
    accepted.filter(({ kind }) => kind === "CapabilityAccepted").map(({ stepId }) => stepId),
    ["implementer", "implementer", "implementer"],
  );
  assert.deepEqual(
    accepted
      .filter(({ kind }) => kind === "CapabilityAccepted")
      .map(({ payload }) => payload.implementation),
    [
      `(input) => input.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "")`,
      `(input) => input.trim().toLowerCase().normalize("NFD").replace(/[\\u0300-\\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "")`,
      `function slugify(input) { return input.trim().toLowerCase().normalize("NFD").replace(/[\\u0300-\\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, ""); }`,
    ],
  );
  assert.deepEqual(
    accepted.filter(({ kind }) => kind === "StructuredOutputAccepted").map(({ stepId }) => stepId),
    ["reviewer", "reviewer"],
  );
  const implementationStates = accepted.filter(
    ({ kind, stepId }) => kind === "StepCompleted" && stepId === "implementer",
  );
  assert.equal(implementationStates.length, 3);
  assert.deepEqual(
    implementationStates.map(({ payload }) => {
      const state = JSON.parse(payload.json);
      return [state.verification, state.review];
    }),
    [
      [null, null],
      [null, null],
      [null, null],
    ],
  );
  const verificationStates = accepted.filter(
    ({ kind, stepId }) => kind === "StepCompleted" && stepId === "verification",
  );
  assert.deepEqual(
    verificationStates.map(({ payload }) => JSON.parse(payload.json).verification.passed),
    [false, true, true],
  );
});
