import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { z } from "zod";

const directory = mkdtempSync(join(tmpdir(), "tandem-workspace-runtime-"));
const mode = process.argv[2] ?? "execute";
const logPath = join(directory, "requests.jsonl");
const server = spawn(
  process.execPath,
  [
    new URL("openai-server-child.mjs", import.meta.url).pathname,
    logPath,
    mode === "parameterized" ? "workspace-parameterized" : "workspace",
  ],
  { stdio: ["ignore", "pipe", "inherit"] },
);
const port = await new Promise((resolve, reject) => {
  server.once("error", reject);
  server.stdout.once("data", (data) => resolve(Number(data.toString().trim())));
});
const cleanup = async () => {
  if (server.exitCode === null && server.signalCode === null) {
    const exited = new Promise((resolve) => server.once("exit", resolve));
    server.kill("SIGKILL");
    await exited;
  }
  rmSync(directory, { recursive: true, force: true });
};

const { agent, agentTools, agentWorkspace, output, pipeline, route, run } =
  await import("../packages/sdk/dist/index.js");
const State = z.object({ workspacePath: z.string() });
if (mode === "parameterized") {
  writeFileSync(
    join(directory, "capture.mjs"),
    "import { writeFileSync } from 'node:fs'; writeFileSync('received.txt', process.argv[3]);\n",
  );
}
const commands = [
  mode === "parameterized"
    ? {
        name: "run_tests",
        description: "Capture one validated argument.",
        command: "node capture.mjs",
        arguments: [
          {
            name: "value",
            description: "Diagnostic value.",
            flag: "--value",
            pattern: "[\\s\\S]*",
            maxLength: 200,
          },
        ],
      }
    : {
        name: "run_tests",
        description: "Write proof that the fixed command ran.",
        command: `${JSON.stringify(process.execPath)} -e "require('fs').writeFileSync('command-ran.txt','ok')"`,
      },
];
const workspace = agentWorkspace({
  path: (state) => state.workspacePath,
  ...(mode === "missing-catalogue" ? {} : { commands }),
});
if (mode === "mutated-catalogue") {
  commands[0].command = `${JSON.stringify(process.execPath)} -e "require('fs').writeFileSync('mutated-command-ran.txt','unsafe')"`;
}

try {
  const worker = agent({
    id: "worker",
    instructions: "Run the fixed command, then finish.",
    client: {
      kind: "openai-compatible",
      version: 1,
      endpoint: `http://127.0.0.1:${port}/v1`,
      model: "gpt-5.6-sol",
      wireApi: "completions",
    },
    message: () => "Run the fixed command.",
    workspace: workspace.withTools([
      mode === "invalid-predicate"
        ? agentTools.when(() => "true", workspace.commands)
        : agentTools.always(workspace.commands),
    ]),
  });
  const done = output({ id: "done", summary: () => "done" });
  const graph = pipeline({
    name: "workspace-runtime",
    state: State,
    nodes: [worker, done],
    start: worker,
    routes: [route({ from: worker, outcome: "success", to: done, label: "done" })],
    outputs: [done],
  });
  await run(graph, { workspacePath: directory });
  console.log(
    JSON.stringify({
      commandRan: existsSync(join(directory, "command-ran.txt")),
      received: existsSync(join(directory, "received.txt"))
        ? readFileSync(join(directory, "received.txt"), "utf8")
        : null,
      marker: existsSync(join(directory, "marker")),
      mutatedCommandRan: existsSync(join(directory, "mutated-command-ran.txt")),
    }),
  );
} catch (error) {
  console.log(JSON.stringify({ error: error.message }));
} finally {
  await cleanup();
}
process.exit(0);
