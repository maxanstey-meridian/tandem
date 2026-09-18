import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { closeProjectWatchers, configWorkspace, watchProjectTypescript } from "../src/watch.js";

const wait = (milliseconds: number) => new Promise((resolve) => setTimeout(resolve, milliseconds));
test.afterEach(async () => closeProjectWatchers());
test("TypeScript watcher advances only after debounced relevant source changes", async () => {
  const root = await mkdtemp(join(tmpdir(), "studio-watch-"));
  const generation = watchProjectTypescript(root);
  await generation.ready;
  assert.equal(generation(), 0);
  await writeFile(join(root, "notes.txt"), "not source");
  await wait(250);
  assert.equal(generation(), 0);
  await writeFile(join(root, "pipeline.ts"), "export const changed = true;");
  await wait(300);
  assert.equal(generation(), 1);
});

test("explicit config watches its application workspace instead of invocation CWD", async () => {
  const invocation = await mkdtemp(join(tmpdir(), "studio-invocation-"));
  const application = await mkdtemp(join(tmpdir(), "studio-explicit-app-"));
  const config = join(application, "tandem.config.ts");

  const root = configWorkspace(config);
  assert.equal(root, application);
  assert.notEqual(root, invocation);
  const generation = watchProjectTypescript(root);
  await generation.ready;
  assert.equal(generation(), 0);
  await writeFile(join(invocation, "outside.ts"), "export const ignored = true;\n");
  await wait(250);
  assert.equal(generation(), 0);
  await writeFile(join(application, "pipeline.ts"), "export const changed = true;\n");
  await wait(300);
  assert.equal(generation(), 1);
});
