import { randomUUID } from "node:crypto";
import { existsSync, rmSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import { z } from "zod";
import {
  inspectAccepted,
  interaction,
  output,
  pipeline,
  route,
  run,
  stage,
} from "../packages/sdk/dist/index.js";

const mode = process.argv[2];
const errorLedgerPath = `/tmp/tandem-sdk-error-${process.pid}.sqlite3`;
const State = z.object({ count: z.number().int(), done: z.boolean() });
const done = output({ id: "done", summary: (state) => String(state.count) });
let abortObserved = false;
let mutatedAfterAbort = false;
let handlerCompleted = false;
let interactionApplied = false;
const make = (execute, name = mode) => {
  const work = stage({ id: "work", execute, persist: true });
  return pipeline({
    name,
    state: State,
    nodes: [work, done],
    start: work,
    routes: [route({ from: work, to: done, label: "done" })],
    outputs: [done],
    persist: true,
  });
};

try {
  if (mode === "invalid") {
    await run(
      make((state) => state),
      { count: "bad", done: false },
    );
  } else if (mode === "failure") {
    await run(
      make(() => {
        throw new Error("callback exploded");
      }),
      { count: 0, done: false },
      { ledgerPath: errorLedgerPath },
    );
  } else if (mode === "cancel") {
    const cancellation = new AbortController();
    await run(
      make(async (state, { signal }) => {
        if (signal.aborted) {
          abortObserved = true;
          throw signal.reason;
        }
        await new Promise((resolve, reject) => {
          const abort = setTimeout(() => cancellation.abort(new Error("cancelled in stage")), 20);
          const timer = setTimeout(() => {
            mutatedAfterAbort = true;
            resolve();
          }, 500);
          signal.addEventListener(
            "abort",
            () => {
              abortObserved = true;
              clearTimeout(abort);
              clearTimeout(timer);
              reject(signal.reason);
            },
            { once: true },
          );
        });
        return { ...state, done: true };
      }),
      { count: 0, done: false },
      { ledgerPath: errorLedgerPath, signal: cancellation.signal },
    );
  } else if (mode === "interaction-cancel") {
    const cancellation = new AbortController();
    const ask = interaction({
      id: "ask",
      requestSchema: z.object({ current: z.number() }),
      responseSchema: z.object({ next: z.number() }),
      request: (state) => ({ current: state.count }),
      handle: (request, { signal }) =>
        new Promise((resolve, reject) => {
          const abort = setTimeout(
            () => cancellation.abort(new Error("cancelled during interaction")),
            20,
          );
          const delayedMutation = setTimeout(() => {
            handlerCompleted = true;
            mutatedAfterAbort = true;
            resolve({ next: request.current + 1 });
          }, 500);
          signal.addEventListener(
            "abort",
            () => {
              abortObserved = true;
              clearTimeout(abort);
              clearTimeout(delayedMutation);
              reject(signal.reason);
            },
            { once: true },
          );
        }),
      apply: (state, response) => {
        interactionApplied = true;
        return { ...state, count: response.next, done: true };
      },
    });
    const graph = pipeline({
      name: mode,
      state: State,
      nodes: [ask, done],
      start: ask,
      routes: [route({ from: ask, to: done, label: "answered" })],
      outputs: [done],
    });
    await run(graph, { count: 0, done: false }, { signal: cancellation.signal });
  } else if (mode === "unknown-inspect") {
    await run(
      make((state) => state),
      { count: 0, done: false },
      { ledgerPath: errorLedgerPath },
    );
    try {
      await inspectAccepted({ ledgerPath: errorLedgerPath, runId: randomUUID() });
      throw new Error("Unknown run inspection unexpectedly succeeded.");
    } catch (error) {
      console.log(JSON.stringify({ error: String(error), operation: error.operation }));
    }
    for (const suffix of ["", "-shm", "-wal"]) {
      rmSync(errorLedgerPath + suffix, { force: true });
    }
    process.exit(0);
  } else if (mode === "failed") {
    const failed = output({ id: "failed", failed: true, summary: () => "declared failure" });
    const work = stage({ id: "work", execute: (state) => state });
    const graph = pipeline({
      name: mode,
      state: State,
      nodes: [work, failed],
      start: work,
      routes: [route({ from: work, to: failed, label: "fail" })],
      outputs: [failed],
      persist: true,
    });
    const result = await run(graph, { count: 0, done: false }, { ledgerPath: errorLedgerPath });
    const db = new DatabaseSync(errorLedgerPath, { readOnly: true });
    const rows = db.prepare("select status, ended_at from runs").all();
    db.close();
    console.log(
      JSON.stringify({
        succeeded: result.succeeded,
        statuses: rows.map((row) => row.status),
        terminalized: rows.every((row) => row.ended_at !== null),
      }),
    );
    for (const suffix of ["", "-shm", "-wal"]) {
      rmSync(errorLedgerPath + suffix, { force: true });
    }
    process.exit(0);
  } else if (mode === "interaction") {
    const ask = interaction({
      id: "ask",
      requestSchema: z.object({ current: z.number() }),
      responseSchema: z.object({ next: z.number() }),
      request: (state) => ({ current: state.count }),
      handle: (request) => ({ next: request.current + 1 }),
      apply: (state, response) => ({ ...state, count: response.next, done: true }),
    });
    const graph = pipeline({
      name: mode,
      state: State,
      nodes: [ask, done],
      start: ask,
      routes: [route({ from: ask, to: done, label: "answered" })],
      outputs: [done],
    });
    const result = await run(graph, { count: 4, done: false });
    console.log(JSON.stringify({ count: result.state.count, done: result.state.done }));
    process.exit(0);
  } else if (mode === "interaction-chain") {
    const first = interaction({
      id: "first",
      requestSchema: z.object({ current: z.number() }),
      responseSchema: z.object({ next: z.number() }),
      request: (state) => ({ current: state.count }),
      handle: (request) => ({ next: request.current + 1 }),
      apply: (state, response) => ({ ...state, count: response.next }),
    });
    const second = interaction({
      id: "second",
      requestSchema: z.object({ current: z.number() }),
      responseSchema: z.object({ next: z.number() }),
      request: (state) => ({ current: state.count }),
      handle: (request) => ({ next: request.current + 1 }),
      apply: (state, response) => ({ ...state, count: response.next, done: true }),
    });
    const graph = pipeline({
      name: mode,
      state: State,
      nodes: [first, second, done],
      start: first,
      routes: [
        route({ from: first, to: second, label: "continue" }),
        route({ from: second, to: done, label: "answered" }),
      ],
      outputs: [done],
    });
    const result = await run(graph, { count: 4, done: false });
    console.log(JSON.stringify({ count: result.state.count, done: result.state.done }));
    process.exit(0);
  } else {
    const count = mode === "soak" ? 25 : mode === "concurrent" ? 8 : mode === "repeated" ? 5 : 1;
    const graph = make((state) => ({ count: state.count + 1, done: true }));
    const ledgerPath = `/tmp/tandem-sdk-${process.pid}.sqlite3`;
    const results =
      mode === "repeated"
        ? await (async () => {
            const values = [];
            for (let i = 0; i < count; i++) {
              values.push(await run(graph, { count: i, done: false }, { ledgerPath }));
            }
            return values;
          })()
        : await Promise.all(
            Array.from({ length: count }, (_, i) =>
              run(graph, { count: i, done: false }, { ledgerPath }),
            ),
          );
    const db = new DatabaseSync(ledgerPath, { readOnly: true });
    const runs = db.prepare("select status, ended_at from runs order by started_at").all();
    const accepted = await inspectAccepted({ ledgerPath, runId: results[0].runId });
    db.close();
    console.log(
      JSON.stringify({
        results: results.length,
        values: results.map((result) => result.state.count),
        statuses: runs.map((item) => item.status),
        terminalized: runs.every((item) => item.ended_at !== null),
        accepted: accepted.length,
        acceptedVersions: accepted.map((item) => item.version),
        acceptedKinds: accepted.map((item) => item.kind),
        sqlite: existsSync(ledgerPath),
      }),
    );
    for (const suffix of ["", "-shm", "-wal"]) {
      rmSync(ledgerPath + suffix, { force: true });
    }
    process.exit(0);
  }
  throw new Error(`${mode} unexpectedly succeeded`);
} catch (error) {
  if (mode === "cancel" || mode === "interaction-cancel") {
    await new Promise((resolve) => setTimeout(resolve, 600));
  }
  let runs = [];
  if (existsSync(errorLedgerPath)) {
    const db = new DatabaseSync(errorLedgerPath, { readOnly: true });
    runs = db.prepare("select status, ended_at from runs").all();
    db.close();
  }
  console.log(
    JSON.stringify({
      error: String(error),
      name: error?.name,
      operation: error?.operation,
      cause: error?.cause ? String(error.cause) : null,
      problems: error?.problems ?? null,
      statuses: runs.map((row) => row.status),
      terminalized: runs.every((row) => row.ended_at !== null),
      abortObserved,
      mutatedAfterAbort,
      handlerCompleted,
      interactionApplied,
    }),
  );
  for (const suffix of ["", "-shm", "-wal"]) {
    rmSync(errorLedgerPath + suffix, { force: true });
  }
  process.exit(["invalid", "failure", "cancel", "interaction-cancel"].includes(mode) ? 0 : 1);
}
