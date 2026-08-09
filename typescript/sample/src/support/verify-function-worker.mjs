import vm from "node:vm";

const chunks = [];
for await (const chunk of process.stdin) {
  chunks.push(chunk);
}
const request = JSON.parse(Buffer.concat(chunks).toString("utf8"));
const context = vm.createContext(Object.create(null), {
  codeGeneration: { strings: false, wasm: false },
});

try {
  new vm.Script(`"use strict"; globalThis.__candidate = (${request.source});`).runInContext(
    context,
    { timeout: request.timeoutMs },
  );
  const kind = new vm.Script(
    "[typeof __candidate, __candidate.length, __candidate.constructor.name, Function.prototype.toString.call(__candidate)]",
  ).runInContext(context, { timeout: request.timeoutMs });
  if (
    kind[0] !== "function" ||
    kind[1] !== 1 ||
    kind[2] !== "Function" ||
    kind[3] !== request.source.trim()
  ) {
    throw new Error(
      "Candidate must be exactly one synchronous function expression accepting one input.",
    );
  }

  const cases = request.cases.map(({ input, expected }) => {
    context.__input = input;
    try {
      const actual = new vm.Script("__candidate(__input)").runInContext(context, {
        timeout: request.timeoutMs,
      });
      if (Object.prototype.toString.call(actual) === "[object Promise]") {
        throw new Error("Candidate returned a Promise; a synchronous string result is required.");
      }
      if (typeof actual !== "string") {
        throw new Error(`Candidate returned ${typeof actual}; a string is required.`);
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
    } finally {
      delete context.__input;
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
