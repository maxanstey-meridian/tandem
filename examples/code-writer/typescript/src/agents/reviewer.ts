import { agent, type ChatClient } from "@tandem/sdk";
import { recordReview, ReviewDecision, type State } from "../state.js";

export const reviewerInstructions =
  "Review the exact implementation against the requirements and passing verification evidence. Return Accept or RequestChanges with a concise summary. RequestChanges must include concrete findings.";

export const reviewerMessage = (state: State) =>
  [
    `Requirements: ${JSON.stringify(state.requirements)}`,
    `Exact source: ${state.implementation!.source}`,
    `Passing verification evidence: ${JSON.stringify(state.verification)}`,
  ].join("\n");

export const reviewerAgent = (client: ChatClient) =>
  agent<State, ReviewDecision>({
    id: "reviewer",
    instructions: reviewerInstructions,
    client,
    message: reviewerMessage,
    output: {
      instructions: "Return Accept or RequestChanges with a concise summary and concrete findings.",
      schema: ReviewDecision,
      apply: recordReview,
    },
    persist: true,
  });
