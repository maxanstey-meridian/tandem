import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("ordinary SDK package has no Studio browser, server, loader, or source-editor dependencies", async () => {
  const manifest = JSON.parse(
    await readFile(new URL("../../sdk/package.json", import.meta.url), "utf8"),
  ) as { dependencies?: Record<string, string>; exports?: Record<string, unknown> };
  const dependencies = Object.keys(manifest.dependencies ?? {});
  for (const name of ["nuxt", "vue", "@vue-flow/core", "elkjs", "jiti", "ts-morph", "codemirror"]) {
    assert.equal(dependencies.includes(name), false, `${name} leaked into the SDK`);
  }
  assert.deepEqual(Object.keys(manifest.exports ?? {}).sort(), [".", "./cli"]);
});
