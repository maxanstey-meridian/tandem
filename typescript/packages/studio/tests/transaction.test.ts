import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { formatSourceText, withExactFileRollback } from "../src/edit.js";

test("failed Studio writes restore exact owned bytes and never touch unrelated dirty files", async () => {
  const root = await mkdtemp(join(tmpdir(), "studio-rollback-"));
  const owned = join(root, "pipeline.ts");
  const unrelated = join(root, "dirty.ts");
  const original = Buffer.from([0xef, 0xbb, 0xbf, ...Buffer.from("const value = 'before';\r\n")]);
  const dirty = Buffer.from("// intentional dirty bytes\r\n");
  await writeFile(owned, original);
  await writeFile(unrelated, dirty);
  const attempted = await formatSourceText(owned, "const value = 'invalid';\n");
  await assert.rejects(
    withExactFileRollback(owned, attempted, async () => {
      throw new Error("TypeScript diagnostic");
    }),
    /TypeScript diagnostic/,
  );
  assert.deepEqual(await readFile(owned), original);
  assert.deepEqual(await readFile(unrelated), dirty);
});

test("successful Studio writes use the repository formatter on only the owned file", async () => {
  const root = await mkdtemp(join(tmpdir(), "studio-format-"));
  const owned = join(root, "pipeline.ts");
  const unrelated = join(root, "dirty.ts");
  await writeFile(owned, "const before = true;\n");
  await writeFile(unrelated, "const untouched={value:1}\n");

  const formatted = await formatSourceText(owned, "const value={first:1,second:2}\n");
  await withExactFileRollback(owned, formatted, async () => {});

  assert.equal(await readFile(owned, "utf8"), "const value = { first: 1, second: 2 };\n");
  assert.equal(await readFile(unrelated, "utf8"), "const untouched={value:1}\n");
});

test("failed validation does not roll back over a concurrent external source change", async () => {
  const root = await mkdtemp(join(tmpdir(), "studio-rollback-race-"));
  const owned = join(root, "pipeline.ts");
  const external = "const value = 'external';\n";
  await writeFile(owned, "const value = 'before';\n");

  await assert.rejects(
    withExactFileRollback(owned, "const value = 'studio';\n", async () => {
      await writeFile(owned, external);
      throw new Error("reconstruction failed");
    }),
    /changed externally during validation.*reconstruction failed/,
  );

  assert.equal(await readFile(owned, "utf8"), external);
});
