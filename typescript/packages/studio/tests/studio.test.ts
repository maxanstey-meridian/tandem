import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { discoverConfig } from "../src/discovery.js";
import { editDirectRoute } from "../src/source.js";
test("discovers nearest config and honors explicit paths", async () => {
  const root = await mkdtemp(join(tmpdir(), "tandem-studio-"));
  const child = join(root, "a", "b");
  await mkdir(child, { recursive: true });
  await writeFile(join(root, "tandem.config.ts"), "");
  await writeFile(join(root, "a", "tandem.config.ts"), "");
  assert.equal(await discoverConfig(child), join(root, "a", "tandem.config.ts"));
  assert.equal(
    await discoverConfig(child, "../../tandem.config.ts"),
    join(root, "tandem.config.ts"),
  );
});
test("edits only direct route AST constructs and preserves order", async () => {
  const root = await mkdtemp(join(tmpdir(), "tandem-source-"));
  const file = join(root, "pipeline.ts");
  await writeFile(
    file,
    'const graph = pipeline({ name: "test", routes: [route({ from: a, to: b, label: "first" }), route({ from: b, to: c, label: "second", when: (state) => state.ok })] });',
  );
  const changed = await editDirectRoute(file, "test", {
    kind: "update",
    order: 1,
    to: "a",
    label: "again",
    when: "(state) => state.ready",
  });
  assert.match(changed, /to: a/);
  assert.match(changed, /label: "again"/);
  assert.match(changed, /state\.ready/);
  assert.match(changed, /first/);
});
test("mutates single-quoted pipeline routes and rejects nonliteral pipeline names", async () => {
  const root = await mkdtemp(join(tmpdir(), "tandem-source-literals-"));
  const direct = join(root, "direct.ts");
  await writeFile(
    direct,
    "pipeline({ name: 'test', routes: [route({ from: a, to: b, label: 'first' })] });",
  );

  const changed = await editDirectRoute(direct, "test", {
    kind: "insert",
    order: 1,
    from: "b",
    to: "c",
    label: "second",
  });

  assert.match(changed, /name: ['"]test['"]/);
  assert.match(changed, /from: b/);
  assert.match(changed, /to: c/);
  assert.match(changed, /label: "second"/);

  const nonliteral = join(root, "nonliteral.ts");
  await writeFile(
    nonliteral,
    "const pipelineName = 'test'; pipeline({ name: pipelineName, routes: [route({ from: a, to: b, label: 'first' })] });",
  );
  await assert.rejects(
    () => editDirectRoute(nonliteral, "test", { kind: "delete", order: 0 }),
    /read-only/,
  );
});

test("helper generated routes fail closed", async () => {
  const root = await mkdtemp(join(tmpdir(), "tandem-source-"));
  const file = join(root, "pipeline.ts");
  await writeFile(file, 'pipeline({ name: "test", routes: makeRoutes() })');
  await assert.rejects(
    () => editDirectRoute(file, "test", { kind: "delete", order: 0 }),
    /read-only/,
  );
});
