import type { PipelineInspection } from "@maxanstey-meridian/tandem";
import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { resolveRouteBoundary, validateRouteEdit } from "../src/edit.js";
import { editDirectRoute } from "../src/source.js";
test("all direct insertion boundaries and empty arrays map exactly", () => {
  assert.equal(resolveRouteBoundary([], 0, 0), 0);
  assert.equal(resolveRouteBoundary([0, 1, 2], 0, 3), 0);
  assert.equal(resolveRouteBoundary([0, 1, 2], 1, 3), 1);
  assert.equal(resolveRouteBoundary([0, 1, 2], 2, 3), 2);
  assert.equal(resolveRouteBoundary([0, 1, 2], 3, 3), 3);
});
test("move boundaries account for removal in both directions", () => {
  assert.equal(resolveRouteBoundary([1, 2], 2, 3, 0), 2);
  assert.equal(resolveRouteBoundary([0, 1], 0, 3, 2), 0);
  assert.equal(resolveRouteBoundary([0, 2], 1, 3, 1), 1);
});
test("placement across helper or spread elements fails closed", () => {
  assert.throws(() => resolveRouteBoundary([1], 0, 2), /helper/);
  assert.throws(() => resolveRouteBoundary([0], 1, 2), /helper/);
  assert.throws(() => resolveRouteBoundary([0, 2], 1, 3), /helper/);
  assert.throws(() => resolveRouteBoundary([0, undefined, 2], 1, 3), /ambiguous/);
});
test("source mutation inserts into empty arrays, reconnects, and moves exactly", async () => {
  const root = await mkdtemp(join(tmpdir(), "ordering-")),
    empty = join(root, "empty.ts");
  await writeFile(empty, 'pipeline({ name: "p", routes: [] })');
  const inserted = await editDirectRoute(empty, "p", {
    kind: "insert",
    order: 0,
    from: "first",
    to: "done",
    label: "go",
  });
  assert.match(inserted, /from: first/);
  const file = join(root, "routes.ts");
  await writeFile(
    file,
    'pipeline({ name: "p", routes: [route({ from: a, to: b, label: "one" }), route({ from: b, to: c, label: "two" }), route({ from: c, to: d, label: "three" })] })',
  );
  const reconnected = await editDirectRoute(file, "p", {
    kind: "update",
    order: 1,
    from: "a",
    to: "d",
  });
  assert.match(reconnected, /from: a,\s+to: d/);
  await writeFile(file, reconnected);
  const moved = await editDirectRoute(file, "p", { kind: "move", order: 0, toOrder: 2 });
  assert.ok(moved.indexOf('label: "two"') < moved.indexOf('label: "three"'));
  assert.ok(moved.indexOf('label: "three"') < moved.indexOf('label: "one"'));
});

test("interleaved routes are ordered within their source and outcome group", () => {
  const graph: PipelineInspection = {
    name: "interleaved",
    stateSchema: {},
    start: "a",
    persist: false,
    nodes: [
      { id: "a", kind: "stage", persist: false },
      { id: "b", kind: "stage", persist: false },
      { id: "agent", kind: "agent", persist: false },
      { id: "done", kind: "completion", persist: false },
    ],
    routes: [
      { id: "route:0", source: "a", target: "b", label: "a1", order: 0, conditional: true },
      { id: "route:1", source: "b", target: "done", label: "b1", order: 1, conditional: false },
      { id: "route:2", source: "a", target: "done", label: "a2", order: 2, conditional: false },
      {
        id: "route:3",
        source: "agent",
        target: "done",
        label: "failed",
        order: 3,
        outcome: "failed",
        conditional: false,
      },
      {
        id: "route:4",
        source: "agent",
        target: "done",
        label: "success",
        order: 4,
        outcome: "success",
        conditional: false,
      },
    ],
    outputs: ["done"],
  };
  assert.deepEqual(
    validateRouteEdit({ kind: "insert", order: 1, from: "a", to: "done", label: "between" }, graph),
    { kind: "insert", order: 2, from: "a", to: "done", label: "between" },
  );
  assert.deepEqual(validateRouteEdit({ kind: "move", order: 0, toOrder: 1 }, graph), {
    kind: "move",
    order: 0,
    toOrder: 2,
  });
  assert.deepEqual(validateRouteEdit({ kind: "move", order: 2, toOrder: 0 }, graph), {
    kind: "move",
    order: 2,
    toOrder: 0,
  });
  assert.throws(
    () => validateRouteEdit({ kind: "move", order: 3, toOrder: 1 }, graph),
    /out of range/,
  );
});
