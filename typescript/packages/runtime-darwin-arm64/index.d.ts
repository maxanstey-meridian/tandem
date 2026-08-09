export function runRegisteredGraphAsync(
  definition: string,
  callback: (id: string, state: string, input: string) => Promise<string>,
  signal?: AbortSignal,
): Promise<string>;
export function inspectAcceptedAsync(ledgerPath: string, runId: string): Promise<string>;
