import { watch, type FSWatcher } from "chokidar";
import { dirname, resolve } from "node:path";

interface WatchState {
  generation: number;
  watcher: FSWatcher;
  ready: Promise<void>;
  debounce?: NodeJS.Timeout;
}
export interface GenerationReader {
  (): number;
  readonly ready: Promise<void>;
}
const projects = new Map<string, WatchState>();

export function configWorkspace(config: string): string {
  return dirname(resolve(config));
}

export function watchProjectTypescript(root: string): GenerationReader {
  const key = resolve(root);
  let state = projects.get(key);
  if (!state) {
    const watcher = watch(key, {
      ignoreInitial: true,
      ignored: (path) =>
        path.includes("node_modules") || path.includes("/.nuxt/") || path.includes("/dist/"),
    });
    let initialized = false;
    const ready = new Promise<void>((resolveReady) =>
      watcher.once("ready", () => {
        initialized = true;
        resolveReady();
      }),
    );
    const created: WatchState = { generation: 0, watcher, ready };
    const changed = (path: string) => {
      if (!initialized || !path.endsWith(".ts")) {
        return;
      }
      if (created.debounce) {
        clearTimeout(created.debounce);
      }
      created.debounce = setTimeout(() => {
        created.generation += 1;
      }, 150);
    };
    created.watcher.on("add", changed).on("change", changed).on("unlink", changed);
    projects.set(key, created);
    state = created;
  }
  return Object.assign(() => state!.generation, { ready: state.ready });
}

export async function closeProjectWatchers(): Promise<void> {
  await Promise.all([...projects.values()].map((state) => state.watcher.close()));
  projects.clear();
}
