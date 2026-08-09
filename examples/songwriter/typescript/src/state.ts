import { z } from "zod";

export const SongDecision = z.object({
  lyrics: z.string().min(1),
});
export type SongDecision = z.infer<typeof SongDecision>;

export const ProofreaderDecision = z.object({
  accepted: z.boolean(),
  feedback: z.string().min(1),
});
export type ProofreaderDecision = z.infer<typeof ProofreaderDecision>;

export const State = z.object({
  brief: z.string().min(1),
  lyrics: z.string().nullable(),
  lintFeedback: z.string().nullable(),
  proofreaderFeedback: z.string().nullable(),
  revision: z.number().int().nonnegative(),
  proofreaderAccepted: z.boolean().nullable(),
});
export type State = z.infer<typeof State>;

export const recordSong = (state: State, decision: SongDecision): State => ({
  ...state,
  lyrics: decision.lyrics,
  revision: state.revision + 1,
  proofreaderAccepted: null,
});

export const recordProofread = (state: State, decision: ProofreaderDecision): State => ({
  ...state,
  proofreaderFeedback: decision.feedback,
  proofreaderAccepted: decision.accepted,
});
