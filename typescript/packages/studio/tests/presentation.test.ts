import assert from "node:assert/strict";
import test from "node:test";
import {
  mergeStablePositions,
  positionStorageKey,
  positionsForIncomingIdentity,
  ReloadGeneration,
  restoreIncomingPositions,
  retainLastValid,
} from "../src/presentation.js";

test("stable positions survive reload while removed IDs disappear and new IDs use layout", () => {
  assert.deepEqual(
    mergeStablePositions(
      ["survives", "saved", "new"],
      { survives: { x: 1, y: 2 }, removed: { x: 9, y: 9 } },
      { saved: { x: 3, y: 4 } },
      { survives: { x: 10, y: 10 }, saved: { x: 20, y: 20 }, new: { x: 5, y: 6 } },
    ),
    {
      survives: { x: 1, y: 2 },
      saved: { x: 3, y: 4 },
      new: { x: 5, y: 6 },
    },
  );
});
test("initial page publication restores positions using the incoming project and pipeline key", () => {
  let requested = "";
  const restored = restoreIncomingPositions(
    "/project/tandem.config.ts",
    "review",
    ["start", "new"],
    {},
    { start: { x: 50, y: 50 }, new: { x: 8, y: 9 } },
    (key) => {
      requested = key;
      return JSON.stringify({ start: { x: 1, y: 2 }, removed: { x: 3, y: 4 } });
    },
  );
  assert.equal(requested, positionStorageKey("/project/tandem.config.ts", "review"));
  assert.deepEqual(restored, { start: { x: 1, y: 2 }, new: { x: 8, y: 9 } });
});
test("live positions carry over only within the same project and pipeline identity", () => {
  const current = { shared: { x: 1, y: 2 } };
  assert.equal(
    positionsForIncomingIdentity("/a/config.ts", "review", "/a/config.ts", "review", current),
    current,
  );
  assert.deepEqual(
    positionsForIncomingIdentity("/a/config.ts", "review", "/a/config.ts", "replacement", current),
    {},
  );
  assert.deepEqual(
    positionsForIncomingIdentity("/a/config.ts", "review", "/b/config.ts", "review", current),
    {},
  );
});
test("only the newest overlapping reload generation may publish", () => {
  const guard = new ReloadGeneration();
  const older = guard.begin();
  const newer = guard.begin();
  assert.equal(guard.isCurrent(older), false);
  assert.equal(guard.isCurrent(newer), true);
});
test("invalid reload retains the last valid graph and reports diagnostics", () => {
  const graph = { name: "valid" };
  assert.deepEqual(retainLastValid(graph, { ok: false, error: "TypeScript failed" }), {
    graph,
    diagnostic: "TypeScript failed",
  });
  const replacement = { name: "replacement" };
  assert.deepEqual(retainLastValid(graph, { ok: true, graph: replacement }), {
    graph: replacement,
    diagnostic: "",
  });
});
