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
  let pipelineCompleted = false;
  try {
    const { formatResult, ...runOptions } = options;
    const result = await run(graph, initial, { ...runOptions, presentation: "terminal" });
    pipelineCompleted = true;
    const formatted = await formatResult(result);
    await write(process.stdout, `${formatted}\n`);
    exitCode = result.succeeded ? 0 : 1;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    const prefix = pipelineCompleted ? "Pipeline completed, but result output failed: " : "";
    await write(process.stderr, `${prefix}${message.split(/\r?\n/, 1)[0]}\n`);
  } finally {
    closeCli(exitCode);
  }
}

function write(stream: NodeJS.WritableStream, value: string): Promise<void> {
  return new Promise((resolve, reject) =>
    stream.write(value, (error) => (error ? reject(error) : resolve())),
  );
}

export function closeCli(exitCode = 0): never {
  process.exit(exitCode);
}
