import { capability } from "@tandem/sdk";
import { z } from "zod";
import { recordImplementation, type ImplementationCandidate, type State } from "../state.js";

export const submitImplementation = () =>
  capability({
    name: "submit_implementation",
    instructions: "Submit the complete JavaScript implementation and its rationale.",
    schema: z.object({ implementation: z.string().min(1), rationale: z.string().min(1) }),
    apply: (state: State, submission): State =>
      recordImplementation(state, {
        source: submission.implementation,
        rationale: submission.rationale,
      } satisfies ImplementationCandidate),
    summarize: (submission) => submission.rationale,
  });
