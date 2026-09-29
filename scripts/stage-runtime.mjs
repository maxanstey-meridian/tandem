import assert from "node:assert/strict";
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { basename } from "node:path";

const rid = process.argv[2];
assert(rid, "usage: node scripts/stage-runtime.mjs <rid>");
const publish = new URL("../.runtime-publish/", import.meta.url);
const runtime = new URL("../bridge-runtime/", import.meta.url);
const bridge = "Tandem.Bridge";

// The publish's own dependency manifest is the asset list: every runtime and native file of
// the RID-specific target, plus the bridge's JavaScript entry points and host configuration.
const deps = JSON.parse(readFileSync(new URL(`${bridge}.deps.json`, publish), "utf8"));
assert.equal(deps.runtimeTarget.name.split("/")[1], rid, "publish RID does not match");
const assets = new Set(
  ["cjs", "mjs", "d.ts", "deps.json", "runtimeconfig.json"].map((ext) => `${bridge}.${ext}`),
);
for (const library of Object.values(deps.targets[deps.runtimeTarget.name])) {
  for (const group of [library.runtime, library.native]) {
    for (const path of Object.keys(group ?? {})) assets.add(basename(path));
  }
}

rmSync(runtime, { recursive: true, force: true });
mkdirSync(runtime, { recursive: true });
for (const name of assets) {
  const source = new URL(name, publish);
  assert(existsSync(source), `missing publish asset: ${name}`);
  copyFileSync(source, new URL(name, runtime));
}
rmSync(publish, { recursive: true, force: true });
