export class ReloadGeneration {
  #current = 0;
  begin(): number {
    return ++this.#current;
  }
  isCurrent(generation: number): boolean {
    return generation === this.#current;
  }
}
export interface Position {
  readonly x: number;
  readonly y: number;
}
export function positionStorageKey(config: string, pipeline: string): string {
  return `tandem-studio:${config}:${pipeline}`;
}
export function positionsForIncomingIdentity(
  currentConfig: string,
  currentPipeline: string | undefined,
  incomingConfig: string,
  incomingPipeline: string,
  current: Readonly<Record<string, Position>>,
): Readonly<Record<string, Position>> {
  return currentConfig === incomingConfig && currentPipeline === incomingPipeline ? current : {};
}
export function restoreIncomingPositions(
  config: string,
  pipeline: string,
  nodeIds: readonly string[],
  current: Readonly<Record<string, Position>>,
  automatic: Readonly<Record<string, Position>>,
  read: (key: string) => string | null,
  storageKey = positionStorageKey(config, pipeline),
): Readonly<Record<string, Position>> {
  let saved: Readonly<Record<string, Position>> = {};
  try {
    saved = JSON.parse(read(storageKey) ?? "{}") as Readonly<Record<string, Position>>;
  } catch {
    saved = {};
  }
  return mergeStablePositions(nodeIds, current, saved, automatic);
}
export function mergeStablePositions(
  nodeIds: readonly string[],
  current: Readonly<Record<string, Position>>,
  saved: Readonly<Record<string, Position>>,
  automatic: Readonly<Record<string, Position>>,
): Readonly<Record<string, Position>> {
  return Object.fromEntries(
    nodeIds.map((id) => [id, current[id] ?? saved[id] ?? automatic[id] ?? { x: 0, y: 0 }]),
  );
}
export function retainLastValid<T>(
  current: T | undefined,
  result: { readonly ok: true; readonly graph: T } | { readonly ok: false; readonly error: string },
): { readonly graph: T | undefined; readonly diagnostic: string } {
  return result.ok
    ? { graph: result.graph, diagnostic: "" }
    : { graph: current, diagnostic: result.error };
}
