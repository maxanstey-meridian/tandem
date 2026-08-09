export function runRegisteredGraphAsync(
  definition: string,
  syncCallback: (id: string, state: string, input: string) => string,
  asyncCallback: (id: string, state: string, input: string, signal: AbortSignal) => Promise<string>,
  signal?: AbortSignal,
): Promise<string>;
export function inspectAcceptedAsync(ledgerPath: string, runId: string): Promise<string>;
