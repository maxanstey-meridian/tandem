import { run, type Pipeline, type RunOptions, type RunResult } from "./index.js";

export interface RunCliOptions<TState> extends Omit<RunOptions, "presentation"> {
  readonly formatResult: (result: RunResult<TState>) => string | Promise<string>;
}

export async function runCli<TState>(
  graph: Pipeline<TState>,
  initial: unknown,
  options: RunCliOptions<TState>,
): Promise<never> {
  let exitCode = 2;
  let signalExitCode: number | null = null;
  let pipelineCompleted = false;
  const shutdown = new AbortController();
  const requestShutdown = (code: number, signal: NodeJS.Signals) => {
    signalExitCode ??= code;
    shutdown.abort(new Error(`Received ${signal}.`));
  };
  const onSigInt = () => requestShutdown(130, "SIGINT");
  const onSigTerm = () => requestShutdown(143, "SIGTERM");
  process.once("SIGINT", onSigInt);
  process.once("SIGTERM", onSigTerm);
  try {
    const { formatResult, signal, ...runOptions } = options;
    const runSignal = signal ? AbortSignal.any([signal, shutdown.signal]) : shutdown.signal;
    const result = await run(graph, initial, {
      ...runOptions,
      signal: runSignal,
      presentation: "terminal",
    });
    pipelineCompleted = true;
    const formatted = await formatResult(result);
    await write(process.stdout, `${formatted}\n`);
    exitCode = result.succeeded ? 0 : 1;
  } catch (error) {
    if (signalExitCode === null) {
      const prefix = pipelineCompleted ? "Pipeline completed, but result output failed: " : "";
      await write(process.stderr, `${prefix}${formatError(error)}\n`);
    }
  } finally {
    process.removeListener("SIGINT", onSigInt);
    process.removeListener("SIGTERM", onSigTerm);
    closeCli(signalExitCode ?? exitCode);
  }
}

function formatError(error: unknown, seen = new Set<unknown>()): string {
  if (!(error instanceof Error)) {
    return String(error);
  }
  if (seen.has(error)) {
    return error.stack ?? `${error.name}: ${error.message}`;
  }
  seen.add(error);

  const stack = error.stack;
  const detail = stack?.startsWith(`${error.name}: `)
    ? stack.slice(error.name.length + 2)
    : (stack ?? error.message);
  if (error.cause === undefined) {
    return detail;
  }
  const cause = formatError(error.cause, seen);
  return detail.includes(cause) ? detail : `${detail}\nCaused by: ${cause}`;
}

function write(stream: NodeJS.WritableStream, value: string): Promise<void> {
  return new Promise((resolve, reject) =>
    stream.write(value, (error) => (error ? reject(error) : resolve())),
  );
}

export function closeCli(exitCode = 0): never {
  process.exit(exitCode);
}
