import { mkdtemp, rm } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { z } from "zod";
import {
  agent,
  output,
  pipeline,
  route,
  run,
  inspectAccepted,
} from "../packages/sdk/dist/index.js";

const directory = await mkdtemp(join(tmpdir(), "tandem-ledger-tools-"));
const tools = [];
const server = createServer((request, response) => {
  let body = "";
  request.on("data", (chunk) => (body += chunk));
  request.on("end", () => {
    tools.push(...(JSON.parse(body).tools ?? []).map((tool) => tool.function.name));
    response.setHeader("content-type", "text/event-stream");
    response.end(
      `data: ${JSON.stringify({ id: "probe", object: "chat.completion.chunk", created: 1, model: "fixture", choices: [{ index: 0, delta: { role: "assistant", content: "Done." }, finish_reason: null }] })}\n\ndata: ${JSON.stringify({ id: "probe", object: "chat.completion.chunk", created: 1, model: "fixture", choices: [{ index: 0, delta: {}, finish_reason: "stop" }] })}\n\ndata: [DONE]\n\n`,
    );
  });
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
try {
  const probe = agent({
    id: "probe",
    instructions: "Respond briefly.",
    message: () => "Hello",
    client: {
      kind: "openai-compatible",
      version: 1,
      endpoint: `http://127.0.0.1:${server.address().port}/v1`,
      model: "fixture",
      wireApi: "completions",
      verifyModel: false,
    },
  });
  const done = output({ id: "done", summary: () => "Done" });
  const graph = pipeline({
    name: "ledger-tools",
    state: z.object({}),
    nodes: [probe, done],
    start: probe,
    routes: [route({ from: probe, to: done, outcome: "success", label: "done" })],
    outputs: [done],
    persist: true,
  });
  const options = { ledgerPath: join(directory, "ledger.sqlite3") };
  if (process.argv[2] === "enabled") {
    options.enableLedgerTools = true;
  }
  const result = await run(graph, {}, options);
  const accepted = await inspectAccepted({ ledgerPath: options.ledgerPath, runId: result.runId });
  console.log(
    JSON.stringify({ succeeded: result.succeeded, tools, persisted: accepted.length > 0 }),
  );
} finally {
  await new Promise((resolve) => server.close(resolve));
  await rm(directory, { recursive: true, force: true });
}
