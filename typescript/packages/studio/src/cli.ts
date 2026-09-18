#!/usr/bin/env node
import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseStudioArguments } from "./cli-args.js";
import { discoverConfig } from "./discovery.js";

try {
  const { config: explicit } = parseStudioArguments(process.argv.slice(2));
  const config = await discoverConfig(process.cwd(), explicit);
  const sourceDirectory = dirname(fileURLToPath(import.meta.url));
  const root = resolve(sourceDirectory, "..");
  const loaderChild = resolve(
    sourceDirectory,
    fileURLToPath(import.meta.url).endsWith(".ts") ? "loader-child.ts" : "loader-child.js",
  );
  const require = createRequire(import.meta.url);
  const nuxt = resolve(dirname(require.resolve("nuxt/package.json")), "bin/nuxt.mjs");
  const child = spawn(process.execPath, [nuxt, "dev", root], {
    stdio: "inherit",
    env: {
      ...process.env,
      TANDEM_STUDIO_CWD: process.cwd(),
      TANDEM_STUDIO_CONFIG: config,
      TANDEM_STUDIO_LOADER_CHILD: loaderChild,
    },
  });
  for (const signal of ["SIGINT", "SIGTERM"] as const) {
    process.once(signal, () => child.kill(signal));
  }
  child.on("exit", (code) => (process.exitCode = code ?? 1));
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
}
