import type { PipelineInspection } from "@maxanstey-meridian/tandem";
import { fork } from "node:child_process";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
export type LoadResult =
  | { readonly ok: true; readonly graph: PipelineInspection }
  | { readonly ok: false; readonly error: string };
export interface LoadOptions {
  readonly timeoutMs?: number;
}
export function loadPipeline(config: string, options: LoadOptions = {}): Promise<LoadResult> {
  return new Promise((complete) => {
    const timeoutMs = options.timeoutMs ?? 10_000;
    if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
      complete({ ok: false, error: "Pipeline loader timeout must be a positive number." });
      return;
    }
    const sourceMode = fileURLToPath(import.meta.url).endsWith(".ts");
    const child = fork(
      process.env.TANDEM_STUDIO_LOADER_CHILD ??
        resolve(
          dirname(fileURLToPath(import.meta.url)),
          sourceMode ? "loader-child.ts" : "loader-child.js",
        ),
      [config],
      {
        cwd: dirname(config),
        env: process.env,
        silent: true,
        ...(sourceMode ? { execArgv: ["--import", require.resolve("tsx")] } : {}),
      },
    );
    let errors = "";
    let settled = false;
    let response: LoadResult | undefined;
    let timer: NodeJS.Timeout | undefined;
    child.stdout!.resume();
    child.stderr!.on("data", (value) => (errors += String(value)));
    const finish = (result: LoadResult) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      complete(result);
    };
    const terminate = () => {
      if (child.connected) {
        child.disconnect();
      }
      if (!child.killed) {
        child.kill();
      }
    };
    child.on("message", (message) => {
      response = isLoadResult(message)
        ? message
        : { ok: false, error: "Pipeline loader returned an invalid IPC response." };
      terminate();
    });
    child.on("error", (error) => {
      terminate();
      finish({ ok: false, error: error.message });
    });
    child.on("exit", () => {
      finish(
        response ?? { ok: false, error: errors || "Pipeline loader exited without a response." },
      );
    });
    timer = setTimeout(() => {
      terminate();
      finish({
        ok: false,
        error: `Pipeline loading exceeded ${timeoutMs}ms. Check tandem.config.ts imports and synchronous construction for blocking work.`,
      });
    }, timeoutMs);
    timer.unref();
  });
}
function isLoadResult(value: unknown): value is LoadResult {
  if (!value || typeof value !== "object") {
    return false;
  }
  const candidate = value as {
    readonly ok?: unknown;
    readonly graph?: unknown;
    readonly error?: unknown;
  };
  return candidate.ok === true
    ? typeof candidate.graph === "object" && candidate.graph !== null
    : candidate.ok === false && typeof candidate.error === "string";
}
