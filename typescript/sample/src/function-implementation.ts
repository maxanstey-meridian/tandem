import { agent, capability, output, pipeline, route, stage, type ChatClient } from "@tandem/sdk";
import { z } from "zod";
import { verifyFunctionSource } from "./support/verify-function-source.js";

export const Requirements = z.array(z.string().min(1)).min(1);
export type Requirements = z.infer<typeof Requirements>;

export const Implementation = z.object({
  source: z.string().min(1),
  rationale: z.string().min(1),
});
export type Implementation = z.infer<typeof Implementation>;

export const VerificationCase = z.object({
  input: z.string(),
  expected: z.string(),
  actual: z.string().nullable(),
  passed: z.boolean(),
  error: z.string().nullable(),
});

export const Verification = z.object({
  passed: z.boolean(),
  cases: z.array(VerificationCase),
  error: z.string().nullable(),
});
export type Verification = z.infer<typeof Verification>;

export const Review = z
  .object({
    decision: z.enum(["Accept", "RequestChanges"]),
    summary: z.string().min(1),
    findings: z.array(z.string().min(1)),
  })
  .refine((review) => review.decision !== "RequestChanges" || review.findings.length > 0, {
    path: ["findings"],
    message: "RequestChanges requires at least one finding",
  });
export type Review = z.infer<typeof Review>;

export const FunctionImplementationState = z.object({
  requirements: Requirements,
  implementation: Implementation.nullable(),
  verification: Verification.nullable(),
  review: Review.nullable(),
});
export type FunctionImplementationState = z.infer<typeof FunctionImplementationState>;

export const openRouterDs4Client = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "https://openrouter.ai/api/v1",
  model: "deepseek/deepseek-v4-flash-0731",
  wireApi: "completions",
  apiKeyEnvironmentVariable: "OPENROUTER_API_KEY",
} as const satisfies ChatClient;

export const localSolClient = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "http://127.0.0.1:10531/v1",
  model: "gpt-5.6-sol",
  wireApi: "responses",
  reasoningEffort: "low",
  verifyModel: true,
} as const satisfies ChatClient;

export function createFunctionImplementationPipeline(
  clients: { implementer: ChatClient; reviewer: ChatClient } = {
    implementer: openRouterDs4Client,
    reviewer: localSolClient,
  },
) {
  const submitImplementation = capability<FunctionImplementationState, Implementation>({
    name: "submit_implementation",
    schema: Implementation,
    apply: (state, implementation) => ({
      ...state,
      implementation,
      verification: null,
      review: null,
    }),
    summarize: (implementation) => implementation.rationale,
  });
  const implementer = agent<FunctionImplementationState>({
    id: "implementer",
    instructions:
      "Implement the requested function. Submit the actual, complete JavaScript function expression and a concise rationale through submit_implementation. The source must be exactly one synchronous function expression accepting one input and returning a string.",
    client: clients.implementer,
    message: (state) =>
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
      ].join("\n"),
    capabilities: [submitImplementation],
    continueSession: true,
    persist: true,
  });
  const verification = stage<FunctionImplementationState>({
    id: "verification",
    execute: async (state) => ({
      ...state,
      verification: await verifyFunctionSource(state.implementation!.source),
    }),
    persist: true,
  });
  const reviewer = agent<FunctionImplementationState, Review>({
    id: "reviewer",
    instructions:
      "Review the exact implementation against the requirements and passing verification evidence. Return Accept or RequestChanges with a concise summary. RequestChanges must include concrete findings.",
    client: clients.reviewer,
    message: (state) =>
      [
        `Requirements: ${JSON.stringify(state.requirements)}`,
        `Exact source: ${state.implementation!.source}`,
        `Passing verification evidence: ${JSON.stringify(state.verification)}`,
      ].join("\n"),
    output: {
      schema: Review,
      apply: (state, review) => ({ ...state, review }),
    },
    persist: true,
  });
  const done = output<FunctionImplementationState>({
    id: "done",
    summary: (state) => state.review!.summary,
  });
  const failed = output<FunctionImplementationState>({
    id: "failed",
    failed: true,
    summary: () => "An agent failed before the function implementation could be accepted.",
  });

  return pipeline({
    name: "function-implementation-review",
    state: FunctionImplementationState,
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
}
