import type { PipelineInspection } from "@maxanstey-meridian/tandem";
import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { applyRouteEdit, validateRouteEdit } from "../src/edit.js";
import { editRevision, locateOwnership } from "../src/ownership.js";
import { resolveSourceTarget } from "../src/source-open.js";
const graph: PipelineInspection = {
  name: "owned",
  stateSchema: {},
  start: "runtime-a",
  persist: false,
  nodes: [
    { id: "runtime-a", kind: "stage", persist: false },
    { id: "runtime-b", kind: "completion", persist: false },
  ],
  routes: [
    {
      id: "route:0",
      source: "runtime-a",
      target: "runtime-b",
      label: "go",
      order: 0,
      conditional: false,
    },
  ],
  outputs: ["runtime-b"],
};
test("state schema source resolves only direct expressions and unique local bindings", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-state-")),
    config = join(root, "tandem.config.ts");
  await writeFile(
    config,
    'const schema = z.object({ value: z.string() });\nconst first = stage({ id: "runtime-a" }); const done = output({ id: "runtime-b" }); pipeline({ name: "owned", state: schema, nodes: [first, done], start: first, routes: [route({ from: first, to: done, label: "go" })], outputs: [done] });',
  );
  const direct = locateOwnership(config, graph);
  assert.deepEqual(resolveSourceTarget(direct, { kind: "state" }), { file: config, line: 1 });

  const stateFile = join(root, "state.ts");
  await writeFile(stateFile, "export const schema = z.object({ value: z.string() });\n");
  await writeFile(
    config,
    'import { schema } from "./state.js";\nconst first = stage({ id: "runtime-a" }); const done = output({ id: "runtime-b" }); pipeline({ name: "owned", state: schema, nodes: [first, done], start: first, routes: [route({ from: first, to: done, label: "go" })], outputs: [done] });',
  );
  const imported = locateOwnership(config, graph);
  assert.deepEqual(resolveSourceTarget(imported, { kind: "state" }), {
    file: stateFile,
    line: 1,
  });

  await writeFile(
    config,
    'const first = stage({ id: "runtime-a" }); const done = output({ id: "runtime-b" }); pipeline({ name: "owned", state: schemas[current], nodes: [first, done], start: first, routes: [route({ from: first, to: done, label: "go" })], outputs: [done] });',
  );
  const ambiguous = locateOwnership(config, graph);
  assert.equal(resolveSourceTarget(ambiguous, { kind: "state" }), undefined);
  assert.match(ambiguous.state?.reason ?? "", /aliased, helper-generated, or ambiguous/);
});

test("single-quoted direct pipeline and route literals retain ownership", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-single-quotes-")),
    config = join(root, "tandem.config.ts");
  await writeFile(
    config,
    "const first = stage({ id: 'runtime-a' }); const done = output({ id: 'runtime-b' }); pipeline({ name: 'owned', state: schema, nodes: [first, done], start: first, routes: [route({ from: first, to: done, label: 'go', outcome: 'success' })], outputs: [done] });",
  );

  const owner = locateOwnership(config, {
    ...graph,
    routes: [{ ...graph.routes[0]!, outcome: "success" }],
  });

  assert.equal(owner.participants["runtime-a"]?.expression, "first");
  assert.equal(owner.routes[0]?.editable, true);
  assert.equal(owner.routes[0]?.sourceIndex, 0);
});

test("participant references come from pipeline nodes rather than unrelated matching ids", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-")),
    file = join(root, "pipeline.ts"),
    config = join(root, "tandem.config.ts");
  await writeFile(config, "");
  await writeFile(
    file,
    'const unrelated = other({ id: "runtime-a" });\nconst first = stage({ id: "runtime-a", execute: (state) => state });\nconst done = output({ id: "runtime-b" });\npipeline({ name: "owned", state: schema, nodes: [first, done], start: first, routes: [route({ from: first, to: done, label: "go" })], outputs: [done] });',
  );
  const owner = locateOwnership(config, graph);
  assert.equal(owner.participants["runtime-a"]?.expression, "first");
  assert.equal(owner.participants["runtime-a"]?.line, 2);
  assert.deepEqual(owner.participants["runtime-a"]?.callbacks, {
    execute: { file, line: 2 },
  });
  assert.equal(owner.routes[0]?.editable, true);
});
test("imported participant references retain route expressions and open owning callbacks", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-imported-participant-")),
    participants = join(root, "participants.ts"),
    config = join(root, "tandem.config.ts");
  await writeFile(
    participants,
    'export const first = stage({ id: "runtime-a", execute: (state) => state });\nexport const done = output({ id: "runtime-b" });\n',
  );
  await writeFile(
    config,
    'import { first as importedFirst, done } from "./participants.js";\npipeline({ name: "owned", state: schema, nodes: [importedFirst, done], start: importedFirst, routes: [route({ from: importedFirst, to: done, label: "go" })], outputs: [done] });',
  );

  const owner = locateOwnership(config, graph);

  assert.equal(owner.participants["runtime-a"]?.expression, "importedFirst");
  assert.equal(owner.participants["runtime-a"]?.file, participants);
  assert.equal(owner.participants["runtime-a"]?.line, 1);
  assert.deepEqual(owner.participants["runtime-a"]?.callbacks, {
    execute: { file: participants, line: 1 },
  });
  assert.deepEqual(resolveSourceTarget(owner, { kind: "participant", id: "runtime-a" }), {
    file: participants,
    line: 1,
  });
  assert.deepEqual(
    resolveSourceTarget(owner, { kind: "callback", id: "runtime-a", name: "execute" }),
    { file: participants, line: 1 },
  );
  assert.equal(owner.routes[0]?.editable, true);
});
test("direct parallel branch participants expose their declarations and callbacks", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-parallel-branch-")),
    config = join(root, "tandem.config.ts");
  const parallelGraph: PipelineInspection = {
    ...graph,
    start: "parallel",
    nodes: [
      {
        id: "parallel",
        kind: "parallel",
        branches: [
          {
            id: "draft",
            participant: { id: "branch-stage", kind: "stage", persist: false },
          },
          {
            id: "review",
            participant: { id: "branch-agent", kind: "agent", persist: false },
          },
        ],
      },
      graph.nodes[1]!,
    ],
    routes: [
      {
        ...graph.routes[0]!,
        source: "parallel",
        outcome: "success",
      },
    ],
  };
  await writeFile(
    config,
    'const branchStage = stage({ id: "branch-stage", execute: (state) => state });\nconst branchAgent = agent({ id: "branch-agent", message: (state) => state.prompt });\nconst concurrent = parallel({ id: "parallel", branches: { draft: branchStage, review: branchAgent }, merge: (state) => state });\nconst done = output({ id: "runtime-b" });\npipeline({ name: "owned", state: schema, nodes: [concurrent, done], start: concurrent, routes: [route({ from: concurrent, outcome: "success", to: done, label: "go" })], outputs: [done] });',
  );

  const owner = locateOwnership(config, parallelGraph);

  assert.equal(owner.participants["branch-stage"]?.file, config);
  assert.equal(owner.participants["branch-stage"]?.line, 1);
  assert.deepEqual(owner.participants["branch-stage"]?.callbacks, {
    execute: { file: config, line: 1 },
  });
  assert.deepEqual(owner.participants["branch-agent"]?.callbacks, {
    message: { file: config, line: 2 },
  });
  assert.deepEqual(
    resolveSourceTarget(owner, { kind: "callback", id: "branch-agent", name: "message" }),
    { file: config, line: 2 },
  );
});

test("direct conditional routes with identical semantics retain authored order ownership", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-conditional-")),
    config = join(root, "tandem.config.ts");
  const conditionalGraph: PipelineInspection = {
    ...graph,
    routes: [
      { ...graph.routes[0]!, id: "route:0", order: 0, conditional: true },
      { ...graph.routes[0]!, id: "route:1", order: 1, conditional: true },
    ],
  };
  await writeFile(
    config,
    'const first = stage({ id: "runtime-a" }); const done = output({ id: "runtime-b" }); pipeline({ name: "owned", state: schema, nodes: [first, done], start: first, routes: [route({ from: first, to: done, label: "go", when: (state) => state.first }), route({ from: first, to: done, label: "go", when: (state) => state.second })], outputs: [done] });',
  );

  const owner = locateOwnership(config, conditionalGraph);

  assert.equal(owner.routes[0]?.editable, true);
  assert.equal(owner.routes[0]?.sourceIndex, 0);
  assert.match(owner.routes[0]?.predicate ?? "", /state\.first/);
  assert.equal(owner.routes[1]?.editable, true);
  assert.equal(owner.routes[1]?.sourceIndex, 1);
  assert.match(owner.routes[1]?.predicate ?? "", /state\.second/);
});

test("direct routes remain editable beside helper and spread routes", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-")),
    config = join(root, "tandem.config.ts");
  await writeFile(
    config,
    'const first = stage({ id: "runtime-a" }); const done = output({ id: "runtime-b" }); pipeline({ name: "owned", state: schema, nodes: [first, done], start: first, routes: [...helpers, route({ from: first, to: done, label: "go" })], outputs: [done] });',
  );
  const mixedGraph: PipelineInspection = {
    ...graph,
    routes: [
      { ...graph.routes[0]!, id: "route:0", label: "helper", order: 0 },
      { ...graph.routes[0]!, id: "route:1", order: 1 },
    ],
  };
  const owner = locateOwnership(config, mixedGraph);
  assert.equal(owner.routes[0]?.editable, false);
  assert.match(owner.routes[0]?.reason ?? "", /open the routes array/);
  assert.deepEqual(resolveSourceTarget(owner, { kind: "route", order: 0 }), {
    file: config,
    line: 1,
  });
  assert.equal(owner.routes[1]?.editable, true);
  assert.equal(owner.routes[1]?.sourceIndex, 1);
});
test("stale route edits are rejected before a numeric index can target changed source", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-stale-")),
    config = join(root, "tandem.config.ts");
  const original =
    'const first = stage({ id: "runtime-a" }); const done = output({ id: "runtime-b" }); pipeline({ name: "owned", state: schema, nodes: [first, done], start: first, routes: [route({ from: first, to: done, label: "go" })], outputs: [done] });';
  await writeFile(config, original);
  const revision = editRevision(graph, locateOwnership(config, graph));
  const externallyChanged = original.replace("routes: [", "routes: [...helpers, ");
  await writeFile(config, externallyChanged);

  const result = await applyRouteEdit(config, graph, { kind: "delete", order: 0 }, revision);

  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.match(result.error, /changed after this graph was displayed/);
  }
  assert.equal(await readFile(config, "utf8"), externallyChanged);
});

test("helper-generated routes remain read-only but open at the known routes property", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-helper-")),
    config = join(root, "tandem.config.ts");
  await writeFile(
    config,
    'const first = stage({ id: "runtime-a" }); const done = output({ id: "runtime-b" });\npipeline({ name: "owned", state: schema, nodes: [first, done], start: first, routes: makeRoutes(), outputs: [done] });',
  );

  const owner = locateOwnership(config, graph);

  assert.equal(owner.routes[0]?.editable, false);
  assert.deepEqual(resolveSourceTarget(owner, { kind: "route", order: 0 }), {
    file: config,
    line: 2,
  });
});

test("ambiguous aliases fail closed and semantic requirements are validated", async () => {
  const root = await mkdtemp(join(tmpdir(), "ownership-")),
    config = join(root, "tandem.config.ts");
  await writeFile(
    config,
    'const first = stage({ id: "runtime-a" }); const done = output({ id: "runtime-b" }); const nodes = [first, done]; pipeline({ name: "owned", state: schema, nodes, start: first, routes: [route({ from: first, to: done, label: "go" })], outputs: [done] });',
  );
  const owner = locateOwnership(config, graph);
  assert.equal(owner.participants["runtime-a"]?.editable, false);
  assert.throws(
    () =>
      validateRouteEdit(
        { kind: "insert", order: 1, from: "runtime-b", to: "runtime-a", label: "bad" },
        graph,
      ),
    /terminal/,
  );
});
