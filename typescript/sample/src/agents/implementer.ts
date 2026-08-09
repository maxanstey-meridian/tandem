import { agent, type ChatClient } from "@tandem/sdk";
import type { submitImplementation } from "../capabilities/submitImplementation.js";
import type { State } from "../state.js";

export const implementerInstructions =
  "Implement the requested function. Submit the actual, complete JavaScript function expression and a concise rationale through submit_implementation. The source must be exactly one synchronous function expression accepting one input and returning a string.";

export const implementerMessage = (state: State) =>
  [
    `Requirements: ${JSON.stringify(state.requirements)}`,
    state.implementation
      ? `Current implementation: ${JSON.stringify(state.implementation)}`
      : "No implementation has been submitted.",
    state.verification
      ? `Verification feedback to address: ${JSON.stringify(state.verification)}`
      : "No verification feedback is pending.",
    state.review
      ? `Reviewer feedback to address: ${JSON.stringify(state.review)}`
      : "No reviewer feedback is pending.",
  ].join("\n");

export const implementerAgent = (
  client: ChatClient,
  capability: ReturnType<typeof submitImplementation>,
) =>
  agent<State>({
    id: "implementer",
    instructions: implementerInstructions,
    client,
    message: implementerMessage,
    capabilities: [capability],
    continueSession: true,
    persist: true,
  });
