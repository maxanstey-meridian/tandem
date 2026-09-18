import { discoverConfig } from "../../src/discovery";
import { outgoingRouteIndexes, resolveRouteBoundary, validateRouteEdit } from "../../src/edit";
import { loadPipeline } from "../../src/loader";
import { editRevision, locateOwnership } from "../../src/ownership";
export default defineEventHandler(async () => {
  try {
    const config = await discoverConfig(
      process.env.TANDEM_STUDIO_CWD ?? process.cwd(),
      process.env.TANDEM_STUDIO_CONFIG,
    );
    const loaded = await loadPipeline(config);
    if (!loaded.ok) {
      return { config, ...loaded };
    }
    const ownership = locateOwnership(config, loaded.graph);
    const indexes = ownership.routes.map((route) =>
      route.editable ? route.sourceIndex : undefined,
    );
    const safe = (values: readonly (number | undefined)[], boundary: number, removed?: number) => {
      try {
        resolveRouteBoundary(values, boundary, ownership.routeArray?.length ?? -1, removed);
        return true;
      } catch {
        return false;
      }
    };
    const insertionGroups = Object.fromEntries(
      loaded.graph.nodes.flatMap((node) => {
        if (node.kind === "completion" || node.kind === "failure") {
          return [];
        }
        const outcomes =
          node.kind === "agent" || node.kind === "parallel"
            ? (["success", "failed"] as const)
            : ([undefined] as const);
        return outcomes.map((outcome) => {
          const count = outgoingRouteIndexes(loaded.graph, node.id, outcome).length;
          const values = Array.from({ length: count + 1 }, (_, order) => {
            const edit = validateRouteEdit(
              {
                kind: "insert",
                order,
                from: node.id,
                to: loaded.graph.nodes[0]!.id,
                label: "route",
                ...(outcome ? { outcome } : {}),
              },
              loaded.graph,
            );
            return safe(indexes, edit.order);
          });
          return [`${node.id}\u0000${outcome ?? "default"}`, values];
        });
      }),
    );
    const editing = {
      insertions: insertionGroups,
      moves: loaded.graph.routes.map((route, from) => {
        const count = outgoingRouteIndexes(loaded.graph, route.source, route.outcome).length;
        return Array.from({ length: count }, (_, toOrder) => {
          const source = indexes[from];
          const edit = validateRouteEdit({ kind: "move", order: from, toOrder }, loaded.graph);
          return (
            source !== undefined &&
            safe(
              indexes.filter((__, index) => index !== from),
              edit.toOrder,
              source,
            )
          );
        });
      }),
    };
    return {
      config,
      ...loaded,
      ownership,
      editing,
      editRevision: editRevision(loaded.graph, ownership),
    };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
});
