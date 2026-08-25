import { agent, output, pipeline, route, stage, type ChatClient } from "@maxanstey-meridian/tandem";
import { ProofreaderDecision, recordProofread, recordSong, SongDecision, State } from "./state.js";

const message = (state: State) =>
  [
    `Brief: ${state.brief}`,
    `Lyrics: ${state.lyrics ?? "None yet."}`,
    `Lint: ${state.lintFeedback ?? "No lint feedback."}`,
    `Proofreader: ${state.proofreaderFeedback ?? "No proofreader feedback."}`,
  ].join("\n");

export const createPipeline = (clients: { songwriter: ChatClient; proofreader: ChatClient }) => {
  const songwriter = agent<State, SongDecision>({
    id: "songwriter",
    instructions: "Write or revise lyrics from the brief and current feedback.",
    client: clients.songwriter,
    message,
    output: {
      instructions: "Return the complete revised song lyrics.",
      schema: SongDecision,
      apply: recordSong,
    },
  });
  const lint = stage<State>({
    id: "lint",
    execute: (state) => ({
      ...state,
      lintFeedback: state.lyrics?.includes("\n") ? null : "Lyrics must contain more than one line.",
    }),
  });
  const proofreader = agent<State, ProofreaderDecision>({
    id: "proofreader",
    instructions: "Proofread lyrics and either accept them or request changes.",
    client: clients.proofreader,
    message,
    output: {
      instructions: "Return the proofread decision and actionable feedback.",
      schema: ProofreaderDecision,
      apply: recordProofread,
    },
  });
  const complete = output<State>({
    id: "complete",
    summary: (state) => `Song accepted after ${state.revision} revision(s)`,
  });
  const failed = output<State>({
    id: "songwriter-failed",
    failed: true,
    summary: (state) => state.proofreaderFeedback ?? "Songwriting failed",
  });

  return pipeline({
    name: "songwriter",
    state: State,
    nodes: [songwriter, lint, proofreader, complete, failed],
    start: songwriter,
    routes: [
      route({ from: songwriter, to: lint, outcome: "success", label: "song written" }),
      route({ from: songwriter, to: failed, outcome: "failed", label: "agent failed" }),
      route({
        from: lint,
        to: proofreader,
        label: "lint passed",
        when: (state) => state.lintFeedback === null,
      }),
      route({
        from: lint,
        to: songwriter,
        label: "lint failed",
        when: (state) => state.lintFeedback !== null,
      }),
      route({
        from: proofreader,
        to: complete,
        outcome: "success",
        label: "proof accepted",
        when: (state) => state.proofreaderAccepted === true,
      }),
      route({
        from: proofreader,
        to: songwriter,
        outcome: "success",
        label: "changes requested",
        when: (state) => state.proofreaderAccepted === false,
      }),
      route({ from: proofreader, to: failed, outcome: "failed", label: "agent failed" }),
    ],
    outputs: [complete, failed],
  });
};
