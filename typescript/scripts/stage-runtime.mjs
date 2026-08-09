import assert from "node:assert/strict";
import { copyFileSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { runtimeAssets } from "./runtime-assets.mjs";

const publish = new URL("../.runtime-publish/", import.meta.url);
const runtime = new URL("../packages/runtime-darwin-arm64/runtime/", import.meta.url);
rmSync(runtime, { recursive: true, force: true });
mkdirSync(runtime, { recursive: true });

for (const name of runtimeAssets) {
  const source = new URL(name, publish);
  assert(existsSync(source), `missing allowlisted publish asset: ${name}`);
  copyFileSync(source, new URL(name, runtime));
}

assert.deepEqual(readdirSync(runtime).sort(), [...runtimeAssets].sort());
rmSync(publish, { recursive: true, force: true });
