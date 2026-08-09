import { inspectAccepted, run } from "@tandem/sdk";
import { closeCli } from "@tandem/sdk/cli";
import { randomUUID } from "node:crypto";
import { createFunctionImplementationPipeline } from "./function-implementation.js";

let exitCode = 1;
try {
  if (!process.env.OPENROUTER_API_KEY) {
    throw new Error("OPENROUTER_API_KEY is required; no dogfood success is fabricated.");
  }
  const ledgerPath =
    process.env.TANDEM_LEDGER_PATH ?? `function-implementation-${randomUUID()}.sqlite3`;
  const result = await run(
    createFunctionImplementationPipeline(),
    {
      requirements: [
        "Implement synchronous pure JavaScript slugify(input).",
        "Trim whitespace and lowercase the input.",
        "Remove Unicode diacritics.",
        "Replace runs of non-alphanumeric characters with one hyphen.",
        "Trim edge hyphens and never return repeated hyphens.",
        "Return an empty string when no alphanumeric characters remain.",
      ],
      implementation: null,
      verification: null,
      review: null,
    },
    { ledgerPath, signal: AbortSignal.timeout(180_000) },
  );
  const accepted = await inspectAccepted({ ledgerPath, runId: result.runId });
  console.log(JSON.stringify({ ...result, ledgerPath, accepted }, null, 2));
  exitCode = result.succeeded ? 0 : 1;
} catch (error) {
  console.error(error);
} finally {
  closeCli(exitCode);
}
