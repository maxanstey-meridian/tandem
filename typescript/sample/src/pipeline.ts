import { output, pipeline, route, type ChatClient } from "@tandem/sdk";
import { implementerAgent } from "./agents/implementer.js";
import { reviewerAgent } from "./agents/reviewer.js";
import { submitImplementation } from "./capabilities/submitImplementation.js";
import { verificationStage } from "./stages/verification.js";
import { State } from "./state.js";

export const createPipeline = (clients: { implementer: ChatClient; reviewer: ChatClient }) => {
  const submit = submitImplementation();
  const implementer = implementerAgent(clients.implementer, submit);
  const verification = verificationStage();
  const reviewer = reviewerAgent(clients.reviewer);
  const done = output<State>({ id: "done", summary: (state) => state.review!.summary });
  const failed = output<State>({
    id: "failed",
    failed: true,
    summary: () => "An agent failed before the function implementation could be accepted.",
  });

  return pipeline({
    name: "function-implementation-review",
    state: State,
    nodes: [implementer, verification, reviewer, done, failed],
    start: implementer,
    routes: [
      route({
        from: implementer,
        to: verification,
        outcome: "success",
        label: "implementation submitted",
      }),
      route({ from: implementer, to: failed, outcome: "failed", label: "implementer failed" }),
      route({
        from: verification,
        to: reviewer,
        label: "verification passed",
        when: (state) => state.verification?.passed === true,
      }),
      route({
        from: verification,
        to: implementer,
        label: "verification failed",
        when: (state) => state.verification?.passed === false,
      }),
      route({
        from: reviewer,
        to: implementer,
        outcome: "success",
        label: "changes requested",
        when: (state) => state.review?.decision === "RequestChanges",
      }),
      route({
        from: reviewer,
        to: done,
        outcome: "success",
        label: "accepted",
        when: (state) => state.review?.decision === "Accept",
      }),
      route({ from: reviewer, to: failed, outcome: "failed", label: "reviewer failed" }),
    ],
    outputs: [done, failed],
    persist: true,
  });
};
