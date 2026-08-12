import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const root = new URL("..", import.meta.url).pathname;
const fixture = mkdtempSync(join(tmpdir(), "tandem-packed-consumer-"));
const packetsFixture = mkdtempSync(join(tmpdir(), "tandem-packets-consumer-"));
let runtimeTar;
let loaderTar;
let sdkTar;
let packetsTar;
try {
  const packetsPack = execFileSync("npm", ["pack", "./packages/packets", "--json"], {
    cwd: root,
    encoding: "utf8",
  });
  packetsTar = JSON.parse(packetsPack)[0].filename;
  const packetsMeta = JSON.parse(packetsPack)[0];
  assert(
    !packetsMeta.files.some(
      (file) => file.path.includes("/src/") || file.path.endsWith(".tsbuildinfo"),
    ),
  );
  writeFileSync(
    join(packetsFixture, "package.json"),
    JSON.stringify({
      type: "module",
      dependencies: {
        "@tandem/packets": `file:${join(root, packetsTar)}`,
        zod: "^4.3.6",
      },
    }),
  );
  execFileSync("npm", ["install", "--ignore-scripts"], {
    cwd: packetsFixture,
    stdio: "inherit",
  });
  execFileSync("npm", ["ls", "@tandem/packets"], {
    cwd: packetsFixture,
    stdio: "inherit",
  });
  writeFileSync(
    join(packetsFixture, "consumer.mjs"),
    `
import { parsePacketFile } from "@tandem/packets";
import { existsSync } from "node:fs";
import { z } from "zod";
const input = parsePacketFile("---\\ntitle: packed\\n---\\n\\nContext", z.object({ title: z.string() }).strict());
console.log(JSON.stringify({ title: input.value.title, context: input.context, nativeRuntimeInstalled: existsSync(new URL("node_modules/@tandem/runtime", import.meta.url)) }));
`,
  );
  const packetsOutput = execFileSync("node", ["consumer.mjs"], {
    cwd: packetsFixture,
    encoding: "utf8",
  });
  assert.deepEqual(JSON.parse(packetsOutput.trim()), {
    title: "packed",
    context: "Context",
    nativeRuntimeInstalled: false,
  });

  const runtimePack = execFileSync("npm", ["pack", "./packages/runtime-darwin-arm64", "--json"], {
    cwd: root,
    encoding: "utf8",
  });
  runtimeTar = JSON.parse(runtimePack)[0].filename;
  const loaderPack = execFileSync("npm", ["pack", "./packages/runtime", "--json"], {
    cwd: root,
    encoding: "utf8",
  });
  loaderTar = JSON.parse(loaderPack)[0].filename;
  const sdkPack = execFileSync("npm", ["pack", "./packages/sdk", "--json"], {
    cwd: root,
    encoding: "utf8",
  });
  sdkTar = JSON.parse(sdkPack)[0].filename;
  const runtimeMeta = JSON.parse(runtimePack)[0];
  const loaderMeta = JSON.parse(loaderPack)[0];
  const sdkMeta = JSON.parse(sdkPack)[0];
  assert.equal(
    runtimeMeta.files.filter((file) => file.path.endsWith("libe_sqlite3.dylib")).length,
    1,
  );
  assert(
    !runtimeMeta.files.some((file) =>
      /(?:\.pdb|\.xml)$|resources\.dll$|Generator\.dll$|\.csproj|\/src\//.test(file.path),
    ),
  );
  assert.deepEqual(loaderMeta.files.map((file) => file.path).sort(), [
    "LICENSE",
    "README.md",
    "index.d.ts",
    "index.mjs",
    "package.json",
  ]);
  for (const metadata of [runtimeMeta, loaderMeta, sdkMeta]) {
    assert(metadata.files.some((file) => file.path === "README.md"));
    assert(metadata.files.some((file) => file.path === "LICENSE"));
  }
  assert(
    !sdkMeta.files.some(
      (file) => file.path.includes("/src/") || file.path.endsWith(".tsbuildinfo"),
    ),
  );
  writeFileSync(
    join(fixture, "package.json"),
    JSON.stringify({
      type: "module",
      dependencies: {
        "@tandem/sdk": `file:${join(root, sdkTar)}`,
        "@tandem/runtime": `file:${join(root, loaderTar)}`,
        "@tandem/runtime-darwin-arm64": `file:${join(root, runtimeTar)}`,
        zod: "^4.3.6",
      },
    }),
  );
  execFileSync("npm", ["install", "--ignore-scripts"], { cwd: fixture, stdio: "inherit" });
  execFileSync("npm", ["ls", "@tandem/sdk", "@tandem/runtime", "@tandem/runtime-darwin-arm64"], {
    cwd: fixture,
    stdio: "inherit",
  });
  writeFileSync(
    join(fixture, "consumer.mjs"),
    `
import { inspectAcceptedAsync, runRegisteredGraphAsync } from "@tandem/runtime";
import { existsSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import { interaction, interactions, output, pipeline, route, run, stage } from "@tandem/sdk";
import { closeCli } from "@tandem/sdk/cli";
import { z } from "zod";
const ledgerPath = new URL("packed.sqlite3", import.meta.url).pathname;
if (typeof inspectAcceptedAsync !== "function" || typeof runRegisteredGraphAsync !== "function") throw new Error("runtime loader exports are unavailable");
const State = z.object({ value: z.number() });
const increment = stage({ id: "increment", execute: (state) => ({ value: state.value + 1 }), persist: true });
const confirm = interaction({ id: "confirm", requestSchema: z.object({ value: z.number() }), responseSchema: z.object({ value: z.number() }), request: (state) => state, apply: (_state, response) => response, persist: true });
const done = output({ id: "done", summary: (state) => String(state.value) });
const graph = pipeline({ name: "packed-consumer", state: State, nodes: [increment, confirm, done], start: increment, routes: [route({ from: increment, to: confirm, label: "confirm" }), route({ from: confirm, to: done, label: "done" })], outputs: [done], persist: true });
const handlers = interactions().handle(confirm, ({ value }) => ({ value: value + 1 }));
const result = await run(graph, { value: 1 }, { ledgerPath, interactions: handlers });
const db = new DatabaseSync(ledgerPath, { readOnly: true });
const row = db.prepare("select status, ended_at from runs where run_id = ?").get(result.runId.replaceAll("-", ""));
db.close();
console.log(JSON.stringify({ value: result.state.value, status: row.status, terminalized: row.ended_at !== null, sqlite: existsSync(ledgerPath) }));
await closeCli(0);
`,
  );
  const consumerOutput = execFileSync("node", ["consumer.mjs"], {
    cwd: fixture,
    encoding: "utf8",
    timeout: 10_000,
  });
  assert.deepEqual(JSON.parse(consumerOutput.trim()), {
    value: 3,
    status: "Ready",
    terminalized: true,
    sqlite: true,
  });
  assert(
    readFileSync(
      join(
        fixture,
        "node_modules/@tandem/runtime-darwin-arm64/runtime/Tandem.NodeApiSpike.Bridge.mjs",
      ),
      "utf8",
    ).includes("__dirname"),
  );
} finally {
  rmSync(fixture, { recursive: true, force: true });
  rmSync(packetsFixture, { recursive: true, force: true });
  if (runtimeTar) {
    rmSync(join(root, runtimeTar), { force: true });
  }
  if (loaderTar) {
    rmSync(join(root, loaderTar), { force: true });
  }
  if (sdkTar) {
    rmSync(join(root, sdkTar), { force: true });
  }
  if (packetsTar) {
    rmSync(join(root, packetsTar), { force: true });
  }
}
