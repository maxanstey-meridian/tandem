import { stage } from "@tandem/sdk";
import { assessImplementation } from "../infrastructure/assess-implementation.js";
import { recordVerification, type State } from "../state.js";

export const verificationStage = () =>
  stage<State>({
    id: "verification",
    execute: async (state) =>
      recordVerification(state, await assessImplementation(state.implementation!.source)),
    persist: true,
  });
