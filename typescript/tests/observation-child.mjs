import { rmSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import { z } from "zod";
import {
  inspectAccepted,
  output,
  pipeline,
  route,
  run,
  stage,
} from "../packages/sdk/dist/index.js";

const mode = process.argv[2];
const ledgerPath = `/tmp/tandem-sdk-observation-${process.pid}.sqlite3`;
const State = z.object({ count: z.number().int() });
const done = output({ id: "done", summary: (state) => String(state.count), persist: true });
const work = stage({
  id: "work",
  persist: true,
  execute: (state) => {
    if (mode === "execution-failure") {
      throw new Error("execution failed");
    }
    return { count: state.count + 1 };
  },
});
const graph = pipeline({
  name: `observation-${mode}`,
  state: State,
  nodes: [work, done],
  start: work,
  routes: [route({ from: work, to: done, label: "done" })],
  outputs: [done],
  persist: true,
});

const cleanup = () => {
  for (const suffix of ["", "-shm", "-wal"]) {
    rmSync(ledgerPath + suffix, { force: true });
  }
};

const statuses = () => {
  const db = new DatabaseSync(ledgerPath, { readOnly: true });
  const result = db
    .prepare("select status from runs order by started_at")
    .all()
    .map((row) => row.status);
  db.close();
  return result;
};

try {
  if (mode === "concurrent") {
    const first = [];
    const second = [];
    await Promise.all([
      run(graph, { count: 0 }, { ledgerPath, observe: (event) => first.push(event) }),
      run(graph, { count: 1 }, { ledgerPath, observe: (event) => second.push(event) }),
    ]);
    console.log(JSON.stringify({ counts: [first.length, second.length] }));
    cleanup();
    process.exit(0);
  }

  const events = [];
  let activeObservers = 0;
  let maximumConcurrentObservers = 0;
  try {
    await run(
      graph,
      { count: 0 },
      {
        ledgerPath,
        observe: async (event) => {
          activeObservers += 1;
          maximumConcurrentObservers = Math.max(maximumConcurrentObservers, activeObservers);
          events.push(event);
          await new Promise((resolve) => setTimeout(resolve, 2));
          activeObservers -= 1;
          if (
            (mode === "observer-failure" || mode === "observer-abort-error") &&
            event.kind === "stepCompleted"
          ) {
            if (mode === "observer-abort-error") {
              throw new DOMException("This operation was aborted", "AbortError");
            }
            throw new Error("observer failed");
          }
          if (mode === "execution-failure" && event.kind === "stepFaulted") {
            throw new Error("observer failed");
          }
        },
      },
    );
    console.log(
      JSON.stringify({
        kinds: events.map((event) => event.kind),
        maximumConcurrentObservers,
        statuses: statuses(),
      }),
    );
  } catch (error) {
    const runs = statuses();
    const db = new DatabaseSync(ledgerPath, { readOnly: true });
    const runId = db.prepare("select run_id from runs limit 1").get().run_id;
    db.close();
    const accepted = await inspectAccepted({
      ledgerPath,
      runId: `${runId.slice(0, 8)}-${runId.slice(8, 12)}-${runId.slice(12, 16)}-${runId.slice(16, 20)}-${runId.slice(20)}`,
    });
    console.log(
      JSON.stringify({
        error: String(error),
        name: error?.name,
        statuses: runs,
        persistedCompletion: accepted.some(
          (item) => item.kind === "StepCompleted" && item.stepId === "work",
        ),
      }),
    );
  }
  cleanup();
  process.exit(0);
} catch (error) {
  console.error(error);
  cleanup();
  process.exit(1);
}
