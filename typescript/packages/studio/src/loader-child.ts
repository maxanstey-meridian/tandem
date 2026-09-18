import { inspectPipeline } from "@maxanstey-meridian/tandem";
import { createJiti } from "jiti";
import type { LoadResult } from "./loader.js";

const config = process.argv[2];
if (!config) {
  throw new Error("A config path is required.");
}
try {
  const loaded = (await createJiti(import.meta.url, { interopDefault: true }).import(
    config,
  )) as Record<string, unknown>;
  const tandem = loaded.tandem as { createPipeline?: unknown } | undefined;
  if (!tandem || typeof tandem.createPipeline !== "function") {
    throw new Error("Config must export 'tandem' with a createPipeline function.");
  }
  const pipeline = (tandem.createPipeline as () => unknown)();
  if (pipeline instanceof Promise) {
    throw new Error("tandem.createPipeline must be synchronous.");
  }
  respond(
    {
      ok: true,
      graph: inspectPipeline(pipeline as Parameters<typeof inspectPipeline>[0]),
    },
    0,
  );
} catch (error) {
  respond(
    {
      ok: false,
      error: error instanceof Error ? `${error.name}: ${error.message}` : String(error),
    },
    1,
  );
}

function respond(result: LoadResult, exitCode: number): void {
  if (!process.send) {
    process.exit(exitCode || 1);
  }
  process.send(result, (error) => {
    if (process.connected) {
      process.disconnect();
    }
    process.exit(error ? 1 : exitCode);
  });
}
