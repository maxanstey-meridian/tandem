import assert from "node:assert/strict";
import { existsSync, readdirSync } from "node:fs";
import { runtimeAssets } from "./runtime-assets.mjs";
const runtime = new URL("../packages/runtime-darwin-arm64/runtime/", import.meta.url);
for (const file of [
  "Tandem.NodeApiSpike.Bridge.mjs",
  "Tandem.NodeApiSpike.Bridge.dll",
  "Tandem.NodeApiSpike.Bridge.runtimeconfig.json",
  "Tandem.dll",
  "Tandem.Ledger.dll",
  "Tandem.Terminal.dll",
  "Spectre.Console.dll",
  "Spectre.Console.Ansi.dll",
  "Microsoft.Agents.AI.dll",
  "Microsoft.Extensions.AI.dll",
  "OpenAI.dll",
]) {
  assert(existsSync(new URL(file, runtime)), `missing packaged runtime asset: ${file}`);
}
assert.deepEqual(
  readdirSync(runtime).filter((name) => name.endsWith("sqlite3.dylib")),
  ["libe_sqlite3.dylib"],
);
assert.deepEqual(readdirSync(runtime).sort(), [...runtimeAssets].sort());
