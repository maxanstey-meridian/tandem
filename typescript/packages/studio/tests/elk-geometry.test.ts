import assert from "node:assert/strict";
import test from "node:test";
import { mapElkLayout } from "../src/elk-geometry.js";

test("ELK nodes and routed sections map to Vue Flow positions and orthogonal paths", () => {
  assert.deepEqual(
    mapElkLayout({
      children: [{ id: "a", x: 12, y: 34 }],
      edges: [
        {
          id: "route:0",
          sections: [
            {
              startPoint: { x: 20, y: 30 },
              bendPoints: [
                { x: 50, y: 30 },
                { x: 50, y: 80 },
              ],
              endPoint: { x: 90, y: 80 },
            },
          ],
        },
      ],
    }),
    {
      positions: { a: { x: 12, y: 34 } },
      edgePaths: { "route:0": "M 20 30 L 50 30 L 50 80 L 90 80" },
      edgeAnchors: { "route:0": { x: 50, y: 60 } },
    },
  );
});
