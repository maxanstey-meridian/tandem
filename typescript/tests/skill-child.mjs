import { spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { z } from "zod";

const root = mkdtempSync(join(tmpdir(), "tandem-skill-fixture-"));
const skillDirectory = join(root, "test-skill");
mkdirSync(join(skillDirectory, "references"), { recursive: true });
mkdirSync(join(skillDirectory, "scripts"));
writeFileSync(
  join(skillDirectory, "SKILL.md"),
  "---\nname: test-skill\ndescription: Test discovery.\n---\n\nFollow the TypeScript doctrine.",
);
writeFileSync(join(skillDirectory, "references", "rules.md"), "Prefer explicit TS boundaries.");
writeFileSync(join(skillDirectory, "scripts", "unsafe.sh"), "exit 99");
const logPath = join(root, "requests.jsonl");
const server = spawn(
  process.execPath,
  [new URL("skill-server-child.mjs", import.meta.url).pathname, logPath],
  { stdio: ["ignore", "pipe", "inherit"] },
);
const port = await new Promise((resolve, reject) => {
  server.once("error", reject);
  server.stdout.once("data", (data) => resolve(Number(data.toString().trim())));
});
const { agent, output, pipeline, route, run, skill } =
  await import("../packages/sdk/dist/index.js");
const State = z.object({ reviewed: z.boolean() });
const reviewer = agent({
  id: "reviewer",
  instructions: "Use the test skill, read its rules, then review.",
  client: {
    kind: "openai-compatible",
    version: 1,
    endpoint: `http://127.0.0.1:${port}/v1`,
    model: "fixture",
    wireApi: "completions",
  },
  message: () => "Review this.",
  skills: [skill({ directory: skillDirectory })],
});
const done = output({ id: "done", summary: () => "Complete." });
const graph = pipeline({
  name: "skill-fixture",
  state: State,
  nodes: [reviewer, done],
  start: reviewer,
  routes: [route({ from: reviewer, to: done, outcome: "success", label: "done" })],
  outputs: [done],
});

try {
  const result = await run(graph, { reviewed: false });
  const requests = readFileSync(logPath, "utf8").trim().split("\n").map(JSON.parse);
  const modelRequests = requests.filter((item) => item.url === "/v1/chat/completions");
  console.log(
    JSON.stringify({
      succeeded: result.succeeded,
      tools: modelRequests[0].body.tools.map((tool) => tool.function.name),
      loadedSkill: JSON.stringify(modelRequests[1].body).includes(
        "Follow the TypeScript doctrine.",
      ),
      loadedResource: JSON.stringify(modelRequests[2].body).includes(
        "Prefer explicit TS boundaries.",
      ),
      exposedScript: JSON.stringify(modelRequests).includes("unsafe.sh"),
    }),
  );
  server.kill();
  rmSync(root, { recursive: true, force: true });
  process.exit(0);
} catch (error) {
  console.error(error);
  server.kill();
  rmSync(root, { recursive: true, force: true });
  process.exit(1);
}
