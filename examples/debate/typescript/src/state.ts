import { z } from "zod";

export const DebateArgument = z.object({
  speaker: z.enum(["proposer", "critic"]),
  text: z.string().min(1),
});
export type DebateArgument = z.infer<typeof DebateArgument>;

export const DebateVerdict = z.object({
  value: z.string().min(1),
  reason: z.string().min(1),
});
export type DebateVerdict = z.infer<typeof DebateVerdict>;

export const ProposalDecision = z.object({ text: z.string().min(1) });
export type ProposalDecision = z.infer<typeof ProposalDecision>;

export const CritiqueDecision = z.object({
  accepted: z.boolean(),
  critique: z.string().min(1),
});
export type CritiqueDecision = z.infer<typeof CritiqueDecision>;

export const SubmitVerdict = z.object({
  verdict: z.string().min(1),
  reason: z.string().min(1),
});
export type SubmitVerdict = z.infer<typeof SubmitVerdict>;

export const State = z.object({
  question: z.string().min(1),
  arguments: z.array(DebateArgument),
  round: z.number().int().nonnegative(),
  verdict: DebateVerdict.nullable(),
  critiqueAccepted: z.boolean().nullable(),
});
export type State = z.infer<typeof State>;

export const recordProposal = (state: State, decision: ProposalDecision): State => ({
  ...state,
  arguments: [...state.arguments, { speaker: "proposer", text: decision.text }],
  round: state.round + 1,
  critiqueAccepted: null,
});

export const recordCritique = (state: State, decision: CritiqueDecision): State => ({
  ...state,
  arguments: [...state.arguments, { speaker: "critic", text: decision.critique }],
  critiqueAccepted: decision.accepted,
});

export const recordVerdict = (state: State, submission: SubmitVerdict): State => ({
  ...state,
  verdict: { value: submission.verdict, reason: submission.reason },
});
