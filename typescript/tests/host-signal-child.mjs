import { createServer } from "node:http";
import { setTimeout as delay } from "node:timers/promises";
import { z } from "zod";
import { output, pipeline, route, run, stage } from "../packages/sdk/dist/index.js";

const State = z.object({ completed: z.boolean() });
const work = stage({
  id: "work",
  execute: async (state, { signal }) => {
    process.stdout.write("RUN_STARTED\n");
    await delay(60_000, undefined, { signal });
    return { ...state, completed: true };
  },
});
const done = output({ id: "done", summary: () => "complete" });
const graph = pipeline({
  name: "host-signal",
  state: State,
  nodes: [work, done],
  start: work,
  routes: [route({ from: work, to: done, label: "complete" })],
  outputs: [done],
});

const shutdown = new AbortController();
const server = createServer((_request, response) => response.end("ok"));
const runPromise = run(graph, { completed: false }, { signal: shutdown.signal }).catch((error) => {
  if (!shutdown.signal.aborted) {
    throw error;
  }
});

process.once("SIGTERM", async () => {
  shutdown.abort(new Error("Host received SIGTERM."));
  server.close();
  await runPromise;
  process.stdout.write("HOST_CLEANED_UP\n", () => process.exit(143));
});

server.listen(0, "127.0.0.1", () => process.stdout.write("HOST_STARTED\n"));
