import type { PipelineInspection } from "@maxanstey-meridian/tandem";
import { spawn } from "node:child_process";
import { access, readFile, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { format, type FormatConfig } from "oxfmt";
import { loadPipeline, type LoadResult } from "./loader.js";
import { editRevision, locateOwnership } from "./ownership.js";
import { editDirectRoute, type RouteEdit } from "./source.js";
const require = createRequire(import.meta.url);
const oxfmtConfig = resolve(dirname(fileURLToPath(import.meta.url)), "../studio.oxfmtrc.json");
export function validateRouteEdit(value: unknown, graph: PipelineInspection): RouteEdit {
  if (!value || typeof value !== "object") {
    throw new Error("A route edit object is required.");
  }
  const edit = value as Record<string, unknown>;
  if (!["insert", "delete", "move", "update"].includes(String(edit.kind))) {
    throw new Error("Unknown route edit kind.");
  }
  const allowed: Record<string, readonly string[]> = {
    insert: ["kind", "order", "from", "to", "label", "outcome", "when"],
    delete: ["kind", "order"],
    move: ["kind", "order", "toOrder"],
    update: ["kind", "order", "from", "to", "label", "outcome", "when"],
  };
  const extra = Object.keys(edit).find((key) => !allowed[String(edit.kind)]!.includes(key));
  if (extra) {
    throw new Error(`Unknown route edit property '${extra}'.`);
  }
  const integer = (name: string, max: number) => {
    const result = edit[name];
    if (!Number.isInteger(result) || Number(result) < 0 || Number(result) > max) {
      throw new Error(`${name} is out of range.`);
    }
    return Number(result);
  };
  const order = edit.kind === "insert" ? undefined : integer("order", graph.routes.length - 1);
  if (edit.kind === "delete") {
    return { kind: "delete", order: order! };
  }
  if (edit.kind === "move") {
    const current = graph.routes[order!]!;
    const group = outgoingRouteIndexes(graph, current.source, current.outcome);
    const currentGroupOrder = group.indexOf(order!);
    const toGroupOrder = integer("toOrder", group.length - 1);
    const remaining = graph.routes.map((_, index) => index).filter((index) => index !== order);
    const remainingGroup = group.filter((index) => index !== order);
    const globalBoundary =
      remainingGroup.length === 0
        ? Math.min(order!, remaining.length)
        : toGroupOrder < remainingGroup.length
          ? remaining.indexOf(remainingGroup[toGroupOrder]!)
          : remaining.indexOf(remainingGroup.at(-1)!) + 1;
    if (currentGroupOrder < 0 || globalBoundary < 0) {
      throw new Error("The outgoing route order could not be mapped unambiguously.");
    }
    return { kind: "move", order: order!, toOrder: globalBoundary };
  }
  const current = edit.kind === "update" ? graph.routes[order!] : undefined;
  const from = typeof edit.from === "string" ? edit.from : current?.source,
    to = typeof edit.to === "string" ? edit.to : current?.target;
  if (
    !from ||
    !to ||
    !graph.nodes.some((node) => node.id === from) ||
    !graph.nodes.some((node) => node.id === to)
  ) {
    throw new Error("Route endpoints must be pipeline participants.");
  }
  const source = graph.nodes.find((node) => node.id === from)!;
  if (source.kind === "completion" || source.kind === "failure") {
    throw new Error("A terminal output cannot have outgoing routes.");
  }
  const needsOutcome = source.kind === "agent" || source.kind === "parallel";
  const rawOutcome = edit.outcome === null ? undefined : (edit.outcome ?? current?.outcome);
  const outcome = rawOutcome === "success" || rawOutcome === "failed" ? rawOutcome : undefined;
  if (rawOutcome !== undefined && outcome === undefined) {
    throw new Error("Route outcome must be success or failed.");
  }
  if (needsOutcome && outcome !== "success" && outcome !== "failed") {
    throw new Error("Agent and parallel routes require a success or failed outcome.");
  }
  if (!needsOutcome && outcome !== undefined) {
    throw new Error("Only agent and parallel routes have standard outcomes.");
  }
  const label = typeof edit.label === "string" ? edit.label : current?.label;
  if (!label?.trim()) {
    throw new Error("Route label must be non-blank.");
  }
  const normalizedOrder =
    edit.kind === "insert"
      ? insertionBoundary(
          graph,
          from,
          outcome,
          integer("order", outgoingRouteIndexes(graph, from, outcome).length),
        )
      : order!;
  if (
    edit.when !== undefined &&
    edit.when !== null &&
    (typeof edit.when !== "string" || !edit.when.trim())
  ) {
    throw new Error("Route predicate must be a non-blank TypeScript expression.");
  }
  return edit.kind === "insert"
    ? {
        kind: "insert",
        order: normalizedOrder,
        from,
        to,
        label,
        ...(outcome ? { outcome } : {}),
        ...(typeof edit.when === "string" ? { when: edit.when } : {}),
      }
    : {
        kind: "update",
        order: normalizedOrder,
        ...(edit.from !== undefined ? { from } : {}),
        ...(edit.to !== undefined ? { to } : {}),
        ...(edit.label !== undefined ? { label } : {}),
        ...(edit.outcome !== undefined
          ? { outcome: edit.outcome as "success" | "failed" | null }
          : {}),
        ...(edit.when !== undefined ? { when: edit.when as string | null } : {}),
      };
}
export function outgoingRouteIndexes(
  graph: PipelineInspection,
  source: string,
  outcome: "success" | "failed" | undefined,
): number[] {
  return graph.routes.flatMap((route, index) =>
    route.source === source && route.outcome === outcome ? [index] : [],
  );
}
function insertionBoundary(
  graph: PipelineInspection,
  source: string,
  outcome: "success" | "failed" | undefined,
  groupOrder: number,
): number {
  const group = outgoingRouteIndexes(graph, source, outcome);
  if (group.length === 0) {
    return graph.routes.length;
  }
  return groupOrder < group.length ? group[groupOrder]! : group.at(-1)! + 1;
}
export function resolveRouteBoundary(
  sourceIndexes: readonly (number | undefined)[],
  boundary: number,
  arrayLength: number,
  removed?: number,
): number {
  if (boundary < 0 || boundary > sourceIndexes.length) {
    throw new Error("Route placement boundary is out of range.");
  }
  const shifted = sourceIndexes.map((index) =>
    index === undefined
      ? undefined
      : index >= (removed ?? Number.POSITIVE_INFINITY)
        ? index - 1
        : index,
  );
  const length = arrayLength - (removed === undefined ? 0 : 1);
  if (sourceIndexes.length === 0) {
    if (length === 0 && boundary === 0) {
      return 0;
    }
    throw new Error("Route placement crosses a helper-generated boundary.");
  }
  if (boundary === 0) {
    if (shifted[0] === 0) {
      return 0;
    }
    throw new Error("Route placement before helper-generated routes is ambiguous.");
  }
  if (boundary === sourceIndexes.length) {
    const last = shifted.at(-1);
    if (last !== undefined && last === length - 1) {
      return length;
    }
    throw new Error("Route placement after helper-generated routes is ambiguous.");
  }
  const left = shifted[boundary - 1],
    right = shifted[boundary];
  if (left !== undefined && right !== undefined && left + 1 === right) {
    return right;
  }
  throw new Error("Route placement crosses a helper-generated or ambiguous route.");
}
export async function withExactFileRollback<T>(
  file: string,
  changed: string,
  validate: () => Promise<T>,
  expectedBefore?: Buffer,
): Promise<T> {
  const before = await readFile(file);
  if (expectedBefore && !before.equals(expectedBefore)) {
    throw new Error(`'${file}' changed externally before Studio could write the edit.`);
  }
  try {
    await writeFile(file, changed);
    return await validate();
  } catch (error) {
    const current = await readFile(file);
    if (!current.equals(Buffer.from(changed))) {
      throw new Error(
        `Edit failed, but '${file}' changed externally during validation; Studio left the newer contents intact. ${error instanceof Error ? error.message : String(error)}`,
      );
    }
    await writeFile(file, before);
    throw error;
  }
}

export async function applyRouteEdit(
  config: string,
  graph: PipelineInspection,
  input: unknown,
  expectedRevision: string,
): Promise<LoadResult> {
  let edit: RouteEdit;
  try {
    edit = validateRouteEdit(input, graph);
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
  const ownership = locateOwnership(config, graph);
  if (editRevision(graph, ownership) !== expectedRevision) {
    return {
      ok: false,
      error:
        "The pipeline source changed after this graph was displayed. Reload and retry the edit.",
    };
  }
  const owner = edit.kind === "insert" ? undefined : ownership.routes[edit.order];
  const file = edit.kind === "insert" ? ownership.routeArray?.file : owner?.file;
  if (!file || (edit.kind !== "insert" && !owner?.editable)) {
    return {
      ok: false,
      error: owner?.reason ?? "No unambiguous direct routes array owns this edit.",
    };
  }
  const reference = (id: string) => {
    const item = ownership.participants[id];
    if (!item?.editable || !item.expression) {
      throw new Error(`Participant '${id}' has no unambiguous direct source binding.`);
    }
    return item.expression;
  };
  try {
    let resolved: RouteEdit;
    const indexes = ownership.routes.map((route) =>
      route.editable ? route.sourceIndex : undefined,
    );
    if (edit.kind === "insert") {
      const before = resolveRouteBoundary(indexes, edit.order, ownership.routeArray!.length);
      resolved = { ...edit, order: before, from: reference(edit.from), to: reference(edit.to) };
    } else if (edit.kind === "delete") {
      resolved = { ...edit, order: owner!.sourceIndex! };
    } else if (edit.kind === "move") {
      const source = owner!.sourceIndex!;
      const remaining = indexes.filter((_, index) => index !== edit.order);
      const target = resolveRouteBoundary(
        remaining,
        edit.toOrder,
        ownership.routeArray!.length,
        source,
      );
      resolved = { kind: "move", order: source, toOrder: target };
    } else {
      resolved = {
        ...edit,
        order: owner!.sourceIndex!,
        ...(edit.from ? { from: reference(edit.from) } : {}),
        ...(edit.to ? { to: reference(edit.to) } : {}),
      };
    }
    const ownedBefore = await readFile(file);
    const changed = await editDirectRoute(file, graph.name, resolved);
    const formatted = await formatSourceText(file, changed);
    try {
      return await withExactFileRollback(
        file,
        formatted,
        async () => {
          const diagnostics = await typecheckProject(dirname(config));
          if (diagnostics) {
            throw new Error(diagnostics);
          }
          const loaded = await loadPipeline(config);
          if (!loaded.ok) {
            throw new Error(loaded.error);
          }
          return loaded;
        },
        ownedBefore,
      );
    } catch (error) {
      return {
        ok: false,
        error: `Edit was not saved: ${error instanceof Error ? error.message : String(error)}`,
      };
    }
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
}
export async function formatSourceText(file: string, source: string): Promise<string> {
  const options = JSON.parse(await readFile(oxfmtConfig, "utf8")) as FormatConfig;
  const result = await format(file, source, options);
  if (result.errors.length > 0) {
    throw new Error(result.errors.map((error) => error.message).join("\n"));
  }
  return result.code;
}

export async function typecheckProject(start: string): Promise<string> {
  let directory = start,
    config: string | undefined;
  for (;;) {
    const candidate = resolve(directory, "tsconfig.json");
    try {
      await access(candidate);
      config = candidate;
      break;
    } catch {}
    const parent = dirname(directory);
    if (parent === directory) {
      break;
    }
    directory = parent;
  }
  if (!config) {
    return "No tsconfig.json was found for save validation.";
  }
  return new Promise((done) => {
    const child = spawn(
      process.execPath,
      [require.resolve("typescript/bin/tsc"), "--noEmit", "--project", config!],
      { cwd: dirname(config!), env: process.env },
    );
    let output = "";
    child.stdout.on("data", (value) => (output += value));
    child.stderr.on("data", (value) => (output += value));
    child.on("close", (code) => done(code === 0 ? "" : output || "TypeScript validation failed."));
    child.on("error", (error) => done(error.message));
  });
}
