import { z } from "zod";

export const Requirements = z.array(z.string().min(1)).min(1);
export type Requirements = z.infer<typeof Requirements>;

export const ImplementationCandidate = z.object({
  source: z.string().min(1),
  rationale: z.string().min(1),
});
export type ImplementationCandidate = z.infer<typeof ImplementationCandidate>;

export const VerificationCase = z.object({
  input: z.string(),
  expected: z.string(),
  actual: z.string().nullable(),
  passed: z.boolean(),
  error: z.string().nullable(),
});
export type VerificationCase = z.infer<typeof VerificationCase>;

export const VerificationResult = z.object({
  passed: z.boolean(),
  cases: z.array(VerificationCase),
  error: z.string().nullable(),
});
export type VerificationResult = z.infer<typeof VerificationResult>;

export const ReviewDecision = z
  .object({
    decision: z.enum(["Accept", "RequestChanges"]),
    summary: z.string().min(1),
    findings: z.array(z.string().min(1)),
  })
  .refine((review) => review.decision !== "RequestChanges" || review.findings.length > 0, {
    path: ["findings"],
    message: "RequestChanges requires at least one finding",
  });
export type ReviewDecision = z.infer<typeof ReviewDecision>;

export const State = z.object({
  requirements: Requirements,
  implementation: ImplementationCandidate.nullable(),
  verification: VerificationResult.nullable(),
  review: ReviewDecision.nullable(),
});
export type State = z.infer<typeof State>;

export const recordImplementation = (
  state: State,
  implementation: ImplementationCandidate,
): State => ({ ...state, implementation, verification: null, review: null });

export const recordVerification = (state: State, verification: VerificationResult): State => ({
  ...state,
  verification,
});

export const recordReview = (state: State, review: ReviewDecision): State => ({ ...state, review });
