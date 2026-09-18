import type { PipelineInspection } from "@maxanstey-meridian/tandem";
import ELK from "elkjs/lib/elk.bundled.js";
import assert from "node:assert/strict";
import test from "node:test";
import { elkGraph, modePositionStorageKey, projectSemanticGraph } from "../src/semantic-graph.js";

const graph: PipelineInspection = {
  name: "generic-review",
  stateSchema: {},
  start: "executor",
  persist: false,
  outputs: ["complete", "failed"],
  nodes: [
    { id: "executor", kind: "agent" },
    { id: "alignment-review", kind: "agent" },
    { id: "acceptance-review", kind: "agent" },
    { id: "complete", kind: "completion" },
    { id: "failed", kind: "failure" },
  ],
  routes: [
    {
      id: "r0",
      source: "executor",
      target: "alignment-review",
      label: "ready-for-review",
      order: 0,
      outcome: "success",
      conditional: true,
    },
    {
      id: "r1",
      source: "executor",
      target: "failed",
      label: "execution-failed",
      order: 1,
      outcome: "failed",
      conditional: false,
    },
    {
      id: "r2",
      source: "alignment-review",
      target: "executor",
      label: "revise",
      order: 2,
      outcome: "success",
      conditional: true,
    },
    {
      id: "r3",
      source: "alignment-review",
      target: "acceptance-review",
      label: "aligned",
      order: 3,
      outcome: "success",
      conditional: true,
    },
    {
      id: "r4",
      source: "alignment-review",
      target: "failed",
      label: "review-failed",
      order: 4,
      outcome: "failed",
      conditional: false,
    },
    {
      id: "r5",
      source: "acceptance-review",
      target: "executor",
      label: "correct",
      order: 5,
      outcome: "success",
      conditional: true,
    },
    {
      id: "r6",
      source: "acceptance-review",
      target: "complete",
      label: "accepted",
      order: 6,
      outcome: "success",
      conditional: true,
    },
    {
      id: "r7",
      source: "acceptance-review",
      target: "failed",
      label: "acceptance-failed",
      order: 7,
      outcome: "failed",
      conditional: false,
    },
  ],
};

test("generic lifecycle fixture projects a readable dominant journey and discoverable corrections", () => {
  const view = projectSemanticGraph(graph, "lifecycle");
  assert.deepEqual(view.dominantNodeIds, [
    "executor",
    "alignment-review",
    "acceptance-review",
    "complete",
  ]);
  assert.equal(view.dominantJourneyStatus, "successful-completion");
  assert.equal(view.routes.find((route) => route.id === "r2")?.classification, "correction");
  assert.equal(view.routes.find((route) => route.id === "r5")?.classification, "correction");
  assert.ok(
    view.routes
      .filter((route) => route.classification === "failure")
      .every((route) => !route.visible && route.compacted),
  );
  assert.equal(
    view.nodes.find((node) => node.id === "acceptance-review")?.title,
    "Acceptance review",
  );
  assert.equal(view.nodes.flatMap((node) => node.ports).length, graph.routes.length);
});
test("all routes exposes each authored route once with unique fixed ordered ports", () => {
  const view = projectSemanticGraph(graph, "all");
  assert.deepEqual(
    view.routes.map((route) => route.id),
    graph.routes.map((route) => route.id),
  );
  assert.ok(view.routes.every((route) => route.visible));
  const ports = view.nodes.flatMap((node) => node.ports.map((port) => port.id));
  assert.equal(new Set(ports).size, graph.routes.length);
  const input = elkGraph(view);
  assert.ok(
    input.children.every((node) => node.layoutOptions["elk.portConstraints"] === "FIXED_POS"),
  );
  assert.deepEqual(
    input.edges.map((edge) => edge.sources[0]),
    view.routes.map((route) => route.sourcePort),
  );
  const executor = view.nodes.find((node) => node.id === "executor")!;
  const elkExecutor = input.children.find((node) => node.id === "executor")!;
  for (const port of executor.ports) {
    const elkPort = elkExecutor.ports.find((candidate) => candidate.id === port.id)!;
    assert.equal(elkPort.x + elkPort.width / 2, port.centerX);
    assert.equal(elkPort.y + elkPort.height / 2, port.centerY);
    assert.equal(elkPort.layoutOptions["elk.port.side"], "EAST");
  }
});
test("real ELK routed endpoints coincide with grouped EAST and WEST handles", async () => {
  const view = projectSemanticGraph(graph, "all");
  const result: any = await new ELK().layout(elkGraph(view) as any);
  const executor = result.children.find((node: any) => node.id === "executor");
  for (const routeId of ["r0", "r1"]) {
    const route = view.routes.find((candidate) => candidate.id === routeId)!;
    const port = executor.ports.find((candidate: any) => candidate.id === route.sourcePort);
    const edge = result.edges.find((candidate: any) => candidate.id === route.id);
    const start = edge.sections[0].startPoint;
    if (port.layoutOptions["elk.port.side"] === "EAST") {
      assert.ok(Math.abs(start.x - (executor.x + port.x + port.width)) < 0.001);
      assert.ok(Math.abs(start.y - (executor.y + port.y + port.height / 2)) < 0.001);
    } else {
      assert.ok(Math.abs(start.x - (executor.x + port.x + port.width / 2)) < 0.001);
      assert.ok(Math.abs(start.y - (executor.y + port.y + port.height)) < 0.001);
    }
  }
  const forward = view.routes.find((route) => route.id === "r0")!;
  const target = result.children.find((node: any) => node.id === forward.target);
  const incoming = target.ports.find((port: any) => port.id === forward.targetPort);
  const end = result.edges.find((edge: any) => edge.id === forward.id).sections.at(-1).endPoint;
  assert.ok(Math.abs(end.x - (target.x + incoming.x)) < 0.001);
  assert.ok(Math.abs(end.y - (target.y + incoming.y + incoming.height / 2)) < 0.001);
  const projectedTarget = view.nodes.find((node) => node.id === forward.target)!;
  assert.equal(incoming.x + incoming.width / 2, projectedTarget.incomingCenterX);
  assert.equal(incoming.y + incoming.height / 2, projectedTarget.incomingCenterY);
});

test("conditional-only progress is primary and self loops are correction without inspecting labels", () => {
  const changed = structuredClone(graph);
  changed.routes[2]!.label = "arbitrary vocabulary";
  changed.routes.push({
    id: "self",
    source: "executor",
    target: "executor",
    label: "anything",
    order: 8,
    outcome: "success",
    conditional: false,
  });
  const view = projectSemanticGraph(changed, "all");
  assert.equal(view.routes.find((route) => route.id === "r0")?.classification, "primary");
  assert.equal(view.routes.find((route) => route.id === "self")?.classification, "correction");
});
test("alternatives, multiple outputs, and off-spine returns are structural", () => {
  const expanded = structuredClone(graph);
  expanded.nodes.push(
    { id: "alternate", kind: "stage" },
    { id: "other-complete", kind: "completion" },
  );
  expanded.outputs.push("other-complete");
  expanded.routes.push(
    {
      id: "alt-out",
      source: "alignment-review",
      target: "alternate",
      label: "unrelated words",
      order: 8,
      outcome: "success",
      conditional: false,
    },
    {
      id: "alt-back",
      source: "alternate",
      target: "executor",
      label: "still unrelated",
      order: 9,
      conditional: false,
    },
    {
      id: "long-output",
      source: "alternate",
      target: "other-complete",
      label: "finish elsewhere",
      order: 10,
      conditional: false,
    },
    {
      id: "failed-to-output",
      source: "alternate",
      target: "other-complete",
      label: "failed elsewhere",
      order: 11,
      outcome: "failed",
      conditional: false,
    },
  );
  const view = projectSemanticGraph(expanded, "all");
  assert.equal(view.routes.find((route) => route.id === "alt-out")?.classification, "alternative");
  assert.equal(view.routes.find((route) => route.id === "alt-back")?.classification, "correction");
  assert.equal(view.routes.find((route) => route.id === "long-output")?.classification, "terminal");
  assert.equal(
    view.routes.find((route) => route.id === "failed-to-output")?.classification,
    "failure",
  );
  assert.deepEqual(view.dominantNodeIds, [
    "executor",
    "alignment-review",
    "acceptance-review",
    "complete",
  ]);
});
test("no-success graphs retain deterministic best-effort progress without inventing completion", () => {
  const noSuccess = structuredClone(graph);
  noSuccess.routes = noSuccess.routes.filter((route) => route.id !== "r6");
  const view = projectSemanticGraph(noSuccess, "lifecycle");
  assert.equal(view.dominantNodeIds[0], "executor");
  assert.ok(!view.dominantNodeIds.includes("complete"));
  assert.equal(view.dominantJourneyStatus, "best-effort");
  assert.equal(view.routes.length, noSuccess.routes.length);
});
test("a unique failure exit remains visible in lifecycle mode", () => {
  const unique = structuredClone(graph);
  unique.routes = unique.routes.filter((route) => route.id !== "r4" && route.id !== "r7");
  const failure = projectSemanticGraph(unique, "lifecycle").routes.find(
    (route) => route.id === "r1",
  )!;
  assert.equal(failure.visible, true);
  assert.equal(failure.compacted, false);
});
test("mode positions have separate local presentation keys", () => {
  assert.notEqual(
    modePositionStorageKey("config", "pipeline", "lifecycle"),
    modePositionStorageKey("config", "pipeline", "all"),
  );
});
