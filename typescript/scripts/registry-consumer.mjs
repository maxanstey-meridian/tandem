import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";

const root = new URL("..", import.meta.url).pathname;
const packageVersion = JSON.parse(
  readFileSync(join(root, "packages/sdk/package.json"), "utf8"),
).version;
const fixture = mkdtempSync(join(tmpdir(), "tandem-registry-consumer-"));
const artifacts = join(fixture, "artifacts");
mkdirSync(artifacts);

const port = await availablePort();
const registry = `http://127.0.0.1:${port}/`;
const config = join(fixture, "verdaccio.yaml");
const npmrc = join(fixture, ".npmrc");
writeFileSync(
  config,
  `storage: ${join(fixture, "storage")}\nmax_body_size: 50mb\nauth:\n  htpasswd:\n    file: ${join(fixture, "htpasswd")}\n    max_users: -1\nuplinks:\n  npmjs:\n    url: https://registry.npmjs.org/\npackages:\n  '@maxanstey-meridian/*':\n    access: $all\n    publish: $all\n    unpublish: $all\n  '**':\n    access: $all\n    proxy: npmjs\nlog: { type: stdout, format: pretty, level: warn }\n`,
);
writeFileSync(npmrc, `registry=${registry}\n//127.0.0.1:${port}/:_authToken=local-test\n`);

const verdaccio = spawn(
  process.execPath,
  [
    join(root, "node_modules/verdaccio/bin/verdaccio"),
    "--config",
    config,
    "--listen",
    `127.0.0.1:${port}`,
  ],
  { cwd: root, stdio: ["ignore", "pipe", "pipe"] },
);
let registryErrors = "";
verdaccio.stderr.setEncoding("utf8");
verdaccio.stderr.on("data", (chunk) => {
  registryErrors += chunk;
});

try {
  await waitForRegistry(registry);
  const packageDirectories = [
    "packages/runtime-darwin-arm64",
    "packages/runtime",
    "packages/sdk",
    "packages/packets",
  ];
  for (const directory of packageDirectories) {
    const metadata = JSON.parse(
      execFileSync("npm", ["pack", `./${directory}`, "--pack-destination", artifacts, "--json"], {
        cwd: root,
        encoding: "utf8",
      }),
    )[0];
    execFileSync(
      "npm",
      [
        "publish",
        join(artifacts, metadata.filename),
        "--registry",
        registry,
        "--userconfig",
        npmrc,
        "--tag",
        "alpha",
      ],
      { cwd: root, stdio: "inherit" },
    );
  }

  for (const manager of ["pnpm", "npm"]) {
    proveConsumer(manager, registry, npmrc);
  }
} finally {
  if (verdaccio.exitCode === null) {
    verdaccio.kill("SIGTERM");
    await new Promise((resolve) => verdaccio.once("exit", resolve));
  }
  rmSync(fixture, { recursive: true, force: true });
}

function proveConsumer(manager, registryUrl, userConfig) {
  const directory = join(fixture, manager);
  mkdirSync(directory);
  writeFileSync(join(directory, "package.json"), JSON.stringify({ private: true, type: "module" }));
  const install =
    manager === "pnpm"
      ? [
          "add",
          "--registry",
          registryUrl,
          `@maxanstey-meridian/tandem@${packageVersion}`,
          `@maxanstey-meridian/tandem-packets@${packageVersion}`,
          "zod@^4.3.6",
        ]
      : [
          "install",
          "--registry",
          registryUrl,
          "--userconfig",
          userConfig,
          `@maxanstey-meridian/tandem@${packageVersion}`,
          `@maxanstey-meridian/tandem-packets@${packageVersion}`,
          "zod@^4.3.6",
        ];
  execFileSync(manager, install, { cwd: directory, stdio: "inherit" });
  writeFileSync(
    join(directory, "consumer.mjs"),
    `import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";\nimport { parsePacketFile } from "@maxanstey-meridian/tandem-packets";\nimport { z } from "zod";\nconst State = z.object({ value: z.number() });\nconst increment = stage({ id: "increment", execute: (state) => ({ value: state.value + 1 }) });\nconst done = output({ id: "done", summary: (state) => String(state.value) });\nconst graph = pipeline({ name: "registry-consumer", state: State, nodes: [increment, done], start: increment, routes: [route({ from: increment, to: done, label: "incremented" })], outputs: [done] });\nconst packet = parsePacketFile("---\\ntitle: registry\\n---\\n\\nContext", z.object({ title: z.string() }).strict());\nconst result = await run(graph, { value: 1 });\nconsole.log(JSON.stringify({ succeeded: result.succeeded, value: result.state.value, packetTitle: packet.value.title }));\n`,
  );
  const output = execFileSync(process.execPath, ["consumer.mjs"], {
    cwd: directory,
    encoding: "utf8",
    timeout: 15_000,
  });
  assert.deepEqual(JSON.parse(output.trim()), {
    succeeded: true,
    value: 2,
    packetTitle: "registry",
  });
  for (const packageName of ["@maxanstey-meridian/tandem", "@maxanstey-meridian/tandem-packets"]) {
    const installed = JSON.parse(
      readFileSync(join(directory, "node_modules", packageName, "package.json")),
    );
    assert.equal(installed.version, packageVersion);
  }
}

async function availablePort() {
  const server = createServer();
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  assert(address && typeof address !== "string");
  await new Promise((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve())),
  );
  return address.port;
}

async function waitForRegistry(registryUrl) {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    if (verdaccio.exitCode !== null) {
      throw new Error(`Verdaccio exited before startup.\n${registryErrors}`);
    }
    try {
      const response = await fetch(registryUrl);
      if (response.ok) {
        return;
      }
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`Verdaccio did not become ready.\n${registryErrors}`);
}
