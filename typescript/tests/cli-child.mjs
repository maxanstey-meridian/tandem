import { z } from "zod";
import { runCli } from "../packages/sdk/dist/cli.js";
import { output, pipeline, route, stage } from "../packages/sdk/dist/index.js";

const mode = process.argv[2];
const State = z.object({ value: z.number() });
const terminal = output({
  id: "terminal",
  failed: mode === "declared-failure",
  summary: () => (mode === "declared-failure" ? "declared failure" : "complete"),
});
const work = stage({
  id: "work",
  execute: async (state, { signal }) => {
    if (mode === "fault") {
      throw new Error("fixture fault");
    }
    if (mode === "cancel") {
      await new Promise((resolve) => setTimeout(resolve, 100));
      signal.throwIfAborted();
    }
    return { value: state.value + 1 };
  },
});
const graph = pipeline({
  name: `cli-${mode}`,
  state: State,
  nodes: [work, terminal],
  start: work,
  routes: [route({ from: work, to: terminal, label: "finish" })],
  outputs: [terminal],
});

await runCli(
  graph,
  { value: 0 },
  {
    signal: mode === "cancel" ? AbortSignal.timeout(10) : undefined,
    formatResult: async (result) => {
      if (mode === "format-fault") {
        throw new Error("formatter failed", { cause: new Error("formatter cause") });
      }
      return `FORMAT_MARKER semantic=${result.state.value}`;
    },
  },
);
