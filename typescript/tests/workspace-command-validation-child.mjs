import { agentWorkspace } from "../packages/sdk/dist/index.js";

const base = {
  name: "run_tests",
  description: "Run tests.",
  command: "task test",
};
const candidates = [
  { arguments: 42 },
  { arguments: [42] },
  { arguments: [""] },
  { arguments: ["a".repeat(201)] },
  { arguments: Array.from({ length: 17 }, (_, index) => `arg${index}`) },
  { arguments: [""] },
  { arguments: ["  "] },
  { arguments: ["a", "b", "a".repeat(201)] },
];

const errors = candidates.map((candidate) => {
  try {
    agentWorkspace({
      path: () => "/tmp",
      commands: [{ ...base, ...candidate }],
    });
    return null;
  } catch (error) {
    return { name: error.name, message: error.message };
  }
});

process.stdout.write(JSON.stringify(errors));
