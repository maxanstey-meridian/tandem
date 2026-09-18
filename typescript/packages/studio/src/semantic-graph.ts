import type { PipelineInspection, InspectedNode, InspectedRoute } from "@maxanstey-meridian/tandem";

export const SEMANTIC_CARD = {
  width: 260,
  contentTop: 72,
  groupHeadingHeight: 18,
  routeRowHeight: 30,
  creationRowHeight: 20,
  bottomPadding: 12,
  portSize: 8,
  incomingCenterY: 27,
} as const;

export type PresentationMode = "lifecycle" | "all";
export type RouteClass = "primary" | "terminal" | "correction" | "alternative" | "failure";
export interface SemanticRoute extends InspectedRoute {
  readonly classification: RouteClass;
  readonly sourcePort: string;
  readonly targetPort: string;
  readonly visible: boolean;
  readonly compacted: boolean;
}
export interface RoutePort {
  readonly id: string;
  readonly routeId: string;
  readonly label: string;
  readonly outcome?: "success" | "failed";
  readonly conditional: boolean;
  readonly classification: RouteClass;
  readonly compacted: boolean;
  readonly side: "right";
  readonly centerX: number;
  readonly centerY: number;
}
export interface SemanticNode extends InspectedNode {
  readonly title: string;
  readonly ports: readonly RoutePort[];
  readonly portGroups: Readonly<Record<string, readonly RoutePort[]>>;
  readonly creationPorts: readonly {
    readonly id: string;
    readonly outcome?: "success" | "failed";
  }[];
  readonly incomingPort: string;
  readonly incomingCenterX: number;
  readonly incomingCenterY: number;
  readonly portSize: number;
  readonly width: number;
  readonly height: number;
}
export interface SemanticGraph {
  readonly nodes: readonly SemanticNode[];
  readonly routes: readonly SemanticRoute[];
  readonly dominantNodeIds: readonly string[];
  readonly dominantJourneyStatus: "successful-completion" | "best-effort";
}

export function humanizeId(value: string): string {
  const words = value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[-_]+/g, " ")
    .trim();
  return words ? words[0]!.toUpperCase() + words.slice(1) : value;
}
function portId(route: InspectedRoute): string {
  return `port:${route.source}:${route.outcome ?? "default"}:${route.order}`;
}
function isFailure(route: InspectedRoute, nodes: ReadonlyMap<string, InspectedNode>): boolean {
  return route.outcome === "failed" || nodes.get(route.target)?.kind === "failure";
}
function dominantPath(
  graph: PipelineInspection,
  nodes: ReadonlyMap<string, InspectedNode>,
): {
  routes: InspectedRoute[];
  status: "successful-completion" | "best-effort";
} {
  const outgoing = new Map<string, InspectedRoute[]>();
  for (const route of graph.routes) {
    if (!isFailure(route, nodes)) {
      const group = outgoing.get(route.source) ?? [];
      group.push(route);
      outgoing.set(route.source, group);
    }
  }
  for (const group of outgoing.values()) {
    group.sort((a, b) => a.order - b.order);
  }
  const queue: { node: string; path: InspectedRoute[]; seen: Set<string> }[] = [
    { node: graph.start, path: [], seen: new Set([graph.start]) },
  ];
  let bestEffort: InspectedRoute[] = [];
  while (queue.length) {
    const current = queue.shift()!;
    if (nodes.get(current.node)?.kind === "completion") {
      return { routes: current.path, status: "successful-completion" };
    }
    if (current.path.length > bestEffort.length) {
      bestEffort = current.path;
    }
    for (const route of outgoing.get(current.node) ?? []) {
      if (!current.seen.has(route.target)) {
        queue.push({
          node: route.target,
          path: [...current.path, route],
          seen: new Set([...current.seen, route.target]),
        });
      }
    }
  }
  return { routes: bestEffort, status: "best-effort" };
}

export function projectSemanticGraph(
  graph: PipelineInspection,
  mode: PresentationMode,
): SemanticGraph {
  const nodeMap = new Map(graph.nodes.map((node) => [node.id, node]));
  const dominantJourney = dominantPath(graph, nodeMap);
  const dominant = dominantJourney.routes;
  const primaryIds = new Set(dominant.map((route) => route.id));
  const dominantNodes = [graph.start, ...dominant.map((route) => route.target)];
  const rank = new Map(dominantNodes.map((id, index) => [id, index]));
  const structuralRank = new Map(rank);
  const pending = [...dominantNodes];
  while (pending.length) {
    const source = pending.shift()!;
    for (const route of graph.routes.filter(
      (candidate) => candidate.source === source && !isFailure(candidate, nodeMap),
    )) {
      if (!structuralRank.has(route.target)) {
        structuralRank.set(route.target, (structuralRank.get(source) ?? 0) + 1);
        pending.push(route.target);
      }
    }
  }
  const failureCounts = new Map<string, number>();
  for (const route of graph.routes) {
    if (isFailure(route, nodeMap)) {
      failureCounts.set(route.target, (failureCounts.get(route.target) ?? 0) + 1);
    }
  }
  const routes: SemanticRoute[] = graph.routes.map((route) => {
    let classification: RouteClass;
    if (isFailure(route, nodeMap)) {
      classification = "failure";
    } else if (nodeMap.get(route.target)?.kind === "completion") {
      classification = "terminal";
    } else if (primaryIds.has(route.id)) {
      classification = "primary";
    } else if (
      rank.has(route.target) &&
      structuralRank.has(route.source) &&
      rank.get(route.target)! <= structuralRank.get(route.source)!
    ) {
      classification = "correction";
    } else {
      classification = "alternative";
    }
    const compacted =
      mode === "lifecycle" &&
      classification === "failure" &&
      (failureCounts.get(route.target) ?? 0) > 1;
    return {
      ...route,
      classification,
      sourcePort: portId(route),
      targetPort: `incoming:${route.target}`,
      visible: mode === "all" || !compacted,
      compacted,
    };
  });
  const bySource = new Map<string, SemanticRoute[]>();
  for (const route of routes) {
    const group = bySource.get(route.source) ?? [];
    group.push(route);
    bySource.set(route.source, group);
  }
  const nodes = graph.nodes.map((node): SemanticNode => {
    const outgoing = (bySource.get(node.id) ?? []).sort((a, b) => {
      if (["agent", "parallel"].includes(node.kind) && a.outcome !== b.outcome) {
        return a.outcome === "success" ? -1 : 1;
      }
      return a.order - b.order;
    });
    const portFacts = outgoing.map((route) => ({
      id: route.sourcePort,
      routeId: route.id,
      label: humanizeId(route.label),
      ...(route.outcome ? { outcome: route.outcome } : {}),
      conditional: route.conditional,
      classification: route.classification,
      compacted: route.compacted,
      side: "right" as const,
    }));
    const factGroups = portFacts.reduce<Record<string, typeof portFacts>>((groups, port) => {
      (groups[port.outcome ?? "default"] ??= []).push(port);
      return groups;
    }, {});
    const rowCenters = new Map<string, number>();
    let rowTop = SEMANTIC_CARD.contentTop;
    for (const [outcome, group] of Object.entries(factGroups)) {
      if (outcome !== "default") {
        rowTop += SEMANTIC_CARD.groupHeadingHeight;
      }
      for (const port of group) {
        rowCenters.set(port.id, rowTop + SEMANTIC_CARD.routeRowHeight / 2);
        rowTop += SEMANTIC_CARD.routeRowHeight;
      }
    }
    const creationCount = ["completion", "failure"].includes(node.kind)
      ? 0
      : ["agent", "parallel"].includes(node.kind)
        ? 2
        : 1;
    const cardHeight =
      rowTop + creationCount * SEMANTIC_CARD.creationRowHeight + SEMANTIC_CARD.bottomPadding;
    const ports: RoutePort[] = portFacts.map((port) => ({
      ...port,
      centerX: SEMANTIC_CARD.width + SEMANTIC_CARD.portSize / 2,
      centerY: rowCenters.get(port.id)!,
    }));
    const portGroups = ports.reduce<Record<string, RoutePort[]>>((groups, port) => {
      (groups[port.outcome ?? "default"] ??= []).push(port);
      return groups;
    }, {});
    const outcomes = ["agent", "parallel"].includes(node.kind)
      ? (["success", "failed"] as const)
      : ([undefined] as const);
    const creationPorts = ["completion", "failure"].includes(node.kind)
      ? []
      : outcomes.map((outcome) => ({
          id: `create:${node.id}:${outcome ?? "default"}`,
          ...(outcome ? { outcome } : {}),
        }));
    return {
      ...node,
      title: humanizeId(node.id),
      ports,
      portGroups,
      creationPorts,
      incomingPort: `incoming:${node.id}`,
      incomingCenterX: -SEMANTIC_CARD.portSize / 2,
      incomingCenterY: SEMANTIC_CARD.incomingCenterY,
      portSize: SEMANTIC_CARD.portSize,
      width: SEMANTIC_CARD.width,
      height: cardHeight,
    };
  });
  return {
    nodes,
    routes,
    dominantNodeIds: dominantNodes,
    dominantJourneyStatus: dominantJourney.status,
  };
}

export function elkGraph(projection: SemanticGraph) {
  return {
    id: "root",
    layoutOptions: {
      "elk.algorithm": "layered",
      "elk.direction": "RIGHT",
      "elk.edgeRouting": "ORTHOGONAL",
      "elk.layered.spacing.nodeNodeBetweenLayers": "90",
      "elk.spacing.nodeNode": "55",
      "elk.layered.feedbackEdges": "true",
    },
    children: projection.nodes.map((node) => ({
      id: node.id,
      width: node.width,
      height: node.height,
      layoutOptions: { "elk.portConstraints": "FIXED_POS" },
      ports: [
        {
          id: node.incomingPort,
          width: node.portSize,
          height: node.portSize,
          x: node.incomingCenterX - node.portSize / 2,
          y: node.incomingCenterY - node.portSize / 2,
          layoutOptions: { "elk.port.side": "WEST", "elk.port.index": "0" },
        },
        ...node.ports.map((port, index) => ({
          id: port.id,
          width: SEMANTIC_CARD.portSize,
          height: SEMANTIC_CARD.portSize,
          x: port.centerX - SEMANTIC_CARD.portSize / 2,
          y: port.centerY - SEMANTIC_CARD.portSize / 2,
          layoutOptions: {
            "elk.port.side": "EAST",
            "elk.port.index": String(index),
          },
        })),
      ],
    })),
    edges: projection.routes
      .filter((route) => route.visible)
      .map((route) => ({
        id: route.id,
        sources: [route.sourcePort],
        targets: [route.targetPort],
        layoutOptions: {
          "elk.layered.priority.direction":
            route.classification === "primary" || route.classification === "terminal" ? "10" : "0",
        },
      })),
  };
}

export function modePositionStorageKey(
  config: string,
  pipeline: string,
  mode: PresentationMode,
): string {
  return `tandem-studio:${config}:${pipeline}:${mode}`;
}
