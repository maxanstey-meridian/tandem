export interface ElkPoint {
  readonly x?: number;
  readonly y?: number;
}
export interface ElkSection {
  readonly startPoint?: ElkPoint;
  readonly bendPoints?: readonly ElkPoint[];
  readonly endPoint?: ElkPoint;
}
export interface ElkLayoutResult {
  readonly children?: readonly { readonly id: string; readonly x?: number; readonly y?: number }[];
  readonly edges?: readonly { readonly id: string; readonly sections?: readonly ElkSection[] }[];
}

function routedMidpoint(sections: readonly ElkSection[]): { x: number; y: number } | undefined {
  const segments = sections.flatMap((section) => {
    const points = [section.startPoint, ...(section.bendPoints ?? []), section.endPoint].filter(
      (point): point is ElkPoint => Boolean(point),
    );
    return points.slice(1).map((end, index) => {
      const start = points[index]!;
      const length = Math.hypot((end.x ?? 0) - (start.x ?? 0), (end.y ?? 0) - (start.y ?? 0));
      return { start, end, length };
    });
  });
  const total = segments.reduce((sum, segment) => sum + segment.length, 0);
  if (!segments.length) {
    return undefined;
  }
  let remaining = total / 2;
  for (const segment of segments) {
    if (remaining <= segment.length) {
      const ratio = segment.length === 0 ? 0 : remaining / segment.length;
      return {
        x: (segment.start.x ?? 0) + ((segment.end.x ?? 0) - (segment.start.x ?? 0)) * ratio,
        y: (segment.start.y ?? 0) + ((segment.end.y ?? 0) - (segment.start.y ?? 0)) * ratio,
      };
    }
    remaining -= segment.length;
  }
  const end = segments.at(-1)!.end;
  return { x: end.x ?? 0, y: end.y ?? 0 };
}

export function mapElkLayout(result: ElkLayoutResult) {
  const positions = Object.fromEntries(
    (result.children ?? []).map((node) => [node.id, { x: node.x ?? 0, y: node.y ?? 0 }]),
  );
  const edgePaths = Object.fromEntries(
    (result.edges ?? []).flatMap((edge) => {
      const sections = edge.sections ?? [];
      if (!sections.length) {
        return [];
      }
      const path = sections
        .map((section) => {
          const points = [
            section.startPoint,
            ...(section.bendPoints ?? []),
            section.endPoint,
          ].filter((point): point is ElkPoint => Boolean(point));
          return points
            .map((point, index) => `${index === 0 ? "M" : "L"} ${point.x ?? 0} ${point.y ?? 0}`)
            .join(" ");
        })
        .join(" ");
      return [[edge.id, path] as const];
    }),
  );
  const edgeAnchors = Object.fromEntries(
    (result.edges ?? []).flatMap((edge) => {
      const anchor = routedMidpoint(edge.sections ?? []);
      return anchor ? [[edge.id, anchor] as const] : [];
    }),
  );
  return { positions, edgePaths, edgeAnchors };
}
