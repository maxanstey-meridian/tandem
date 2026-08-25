import { agentWorkspace } from "../packages/sdk/dist/index.js";

const base = {
  name: "run_tests",
  description: "Run tests.",
  command: "task test",
};
const argument = {
  name: "value",
  description: "Value.",
  flag: "--value",
  pattern: ".+",
};
const definitions = [
  { ...argument, name: "bad-name" },
  { ...argument, flag: "--bad flag" },
  { name: "value", description: "Value.", flag: "--value" },
  { ...argument, allowedValues: ["one"] },
  { ...argument, maxLength: 0 },
  { ...argument, pattern: undefined, allowedValues: [] },
  { ...argument, pattern: undefined, allowedValues: [" "] },
  { ...argument, pattern: undefined, allowedValues: ["one", "one"] },
];

const errors = definitions.map((candidate) => {
  try {
    agentWorkspace({
      path: () => "/tmp",
      commands: [{ ...base, arguments: [candidate] }],
    });
    return null;
  } catch (error) {
    return { name: error.name, message: error.message };
  }
});

process.stdout.write(JSON.stringify(errors));
