import { agent, capability, output, pipeline, route, stage, type ChatClient } from "@tandem/sdk";
import {
  CritiqueDecision,
  ProposalDecision,
  State,
  SubmitVerdict,
  recordCritique,
  recordProposal,
  recordVerdict,
  type State as DebateState,
} from "./state.js";

const transcript = (state: DebateState) =>
  state.arguments.length === 0
    ? "No argument has been made yet."
    : state.arguments.map((argument) => `${argument.speaker}: ${argument.text}`).join("\n");

export const createPipeline = (clients: {
  proposer: ChatClient;
  critic: ChatClient;
  judge: ChatClient;
}) => {
  const open = stage<DebateState>({ id: "open", execute: (state) => state });

  const proposer = agent<DebateState, ProposalDecision>({
    id: "proposer",
    instructions:
      "Act as the debate proposer. Make the strongest concise argument answering the question. Revise it when the critic identifies weaknesses.",
    client: clients.proposer,
    message: (state) =>
      `Question: ${state.question}\nRound: ${state.round}\nDebate so far:\n${transcript(state)}`,
    output: {
      instructions: "Return the proposed debate argument.",
      schema: ProposalDecision,
      apply: recordProposal,
    },
    continueSession: true,
  });

  const critic = agent<DebateState, CritiqueDecision>({
    id: "critic",
    instructions:
      "Act as the debate critic. Assess the latest proposal against the question. Accept only a persuasive, well-supported argument; otherwise explain the concrete revision needed.",
    client: clients.critic,
    message: (state) =>
      `Question: ${state.question}\nRound: ${state.round}\nDebate so far:\n${transcript(state)}`,
    output: {
      instructions: "Return the critique and whether the proposal is accepted.",
      schema: CritiqueDecision,
      apply: recordCritique,
    },
    continueSession: true,
  });

  const submitVerdict = capability<DebateState, SubmitVerdict>({
    name: "submit_verdict",
    instructions: "Submit the final debate verdict and the reason for it.",
    schema: SubmitVerdict,
    apply: recordVerdict,
    summarize: (submission) => `Verdict submitted: ${submission.verdict}`,
  });

  const judge = agent<DebateState>({
    id: "judge",
    instructions:
      "Judge the accepted argument against the question. You must conclude by calling submit_verdict with a clear verdict and reason.",
    client: clients.judge,
    message: (state) =>
      `Question: ${state.question}\nAccepted debate transcript:\n${transcript(state)}`,
    capabilities: [submitVerdict],
  });

  const complete = output<DebateState>({
    id: "complete",
    summary: (state) => `Verdict reached after ${state.round} round(s)`,
  });
  const failed = output<DebateState>({
    id: "debate-failed",
    failed: true,
    summary: () => "Debate ended without a verdict",
  });

  return pipeline({
    name: "debate",
    state: State,
    nodes: [open, proposer, critic, judge, complete, failed],
    start: open,
    routes: [
      route({ from: open, to: proposer, label: "debate opened" }),
      route({ from: proposer, to: critic, outcome: "success", label: "argument proposed" }),
      route({ from: proposer, to: failed, outcome: "failed", label: "agent failed" }),
      route({
        from: critic,
        to: proposer,
        outcome: "success",
        when: (state) => state.critiqueAccepted === false,
        label: "revision requested",
      }),
      route({
        from: critic,
        to: judge,
        outcome: "success",
        when: (state) => state.critiqueAccepted === true,
        label: "argument accepted",
      }),
      route({ from: critic, to: failed, outcome: "failed", label: "agent failed" }),
      route({
        from: judge,
        to: complete,
        outcome: "success",
        when: (state) => state.verdict !== null,
        label: "verdict submitted",
      }),
      route({ from: judge, to: failed, outcome: "failed", label: "agent failed" }),
    ],
    outputs: [complete, failed],
  });
};
