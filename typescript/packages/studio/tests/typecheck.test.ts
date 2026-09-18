import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { typecheckProject } from "../src/edit.js";

test("free-text predicates receive inferred state type diagnostics", async () => {
  const root = await mkdtemp(join(tmpdir(), "studio-types-"));
  await writeFile(
    join(root, "tsconfig.json"),
    JSON.stringify({
      compilerOptions: { strict: true, noEmit: true, target: "ES2022" },
      include: ["pipeline.ts"],
    }),
  );
  await writeFile(
    join(root, "pipeline.ts"),
    `type State = { review?: { decision: string } };
const route = (value: { when: (state: State) => boolean }) => value;
route({ when: (state) => state.missing === true });`,
  );
  const diagnostics = await typecheckProject(root);
  assert.match(diagnostics, /Property 'missing' does not exist on type 'State'/);
});
