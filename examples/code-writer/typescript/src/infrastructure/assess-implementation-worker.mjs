import vm from "node:vm";
import { z } from "zod";

const chunks = [];
for await (const chunk of process.stdin) {
  chunks.push(chunk);
}
const request = JSON.parse(Buffer.concat(chunks).toString("utf8"));
const context = vm.createContext(Object.create(null), {
  codeGeneration: { strings: false, wasm: false },
});

const Slugify = z.string().transform((source, issueContext) => {
  try {
    const script = new vm.Script(`"use strict"; (${source})`);
    const implementation = script.runInContext(context, { timeout: 100 });
    if (typeof implementation !== "function") {
      issueContext.addIssue({ code: "custom", message: "JavaScript must evaluate to a function" });
      return z.NEVER;
    }
    return implementation;
  } catch (error) {
    issueContext.addIssue({
      code: "custom",
      message: `Invalid JavaScript: ${error instanceof Error ? error.message : String(error)}`,
    });
    return z.NEVER;
  }
});

try {
  const implementation = Slugify.parse(request.source);
  const cases = request.cases.map(({ input, expected }) => {
    try {
      const actual = implementation(input);
      if (Object.prototype.toString.call(actual) === "[object Promise]") {
        throw new Error("Implementation returned a Promise; a synchronous string is required.");
      }
      if (typeof actual !== "string") {
        throw new Error(`Implementation returned ${typeof actual}; a string is required.`);
      }
      return { input, expected, actual, passed: actual === expected, error: null };
    } catch (error) {
      return {
        input,
        expected,
        actual: null,
        passed: false,
        error: error instanceof Error ? error.message : String(error),
      };
    }
  });
  process.stdout.write(
    JSON.stringify({ passed: cases.every((item) => item.passed), cases, error: null }),
  );
} catch (error) {
  process.stdout.write(
    JSON.stringify({
      passed: false,
      cases: [],
      error: error instanceof Error ? error.message : String(error),
    }),
  );
}
