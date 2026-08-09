import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { test } from "node:test";
import { promisify } from "node:util";
import {
  recordCritique,
  recordProposal,
  recordVerdict,
} from "../../examples/debate/typescript/src/state.ts";
import { recordProofread, recordSong } from "../../examples/songwriter/typescript/src/state.ts";

const exec = promisify(execFile);
const graphs = async () => {
  const { stdout } = await exec(
    "pnpm",
    ["exec", "tsx", new URL("examples-child.ts", import.meta.url).pathname],
    { timeout: 10_000 },
  );
  return JSON.parse(stdout.trim());
};

test("songwriter example models revision facts and its complete graph", async () => {
  const initial = {
    brief: "Write about home.",
    lyrics: null,
    lintFeedback: null,
    proofreaderFeedback: null,
    revision: 0,
    proofreaderAccepted: null,
  };
  const written = recordSong(initial, { lyrics: "First line\nSecond line" });
  const reviewed = recordProofread(written, { accepted: true, feedback: "Accepted." });
  const { songwriter: graph } = await graphs();

  assert.equal(written.revision, 1);
  assert.equal(reviewed.proofreaderAccepted, true);
  assert.deepEqual(graph.nodes, [
    "songwriter",
    "lint",
    "proofreader",
    "complete",
    "songwriter-failed",
  ]);
  assert.equal(graph.routes, 7);
});

test("debate example models proposal, critique, verdict, and revision routes", async () => {
  const initial = {
    question: "Should cities replace parking with public space?",
    arguments: [],
    round: 0,
    verdict: null,
    critiqueAccepted: null,
  };
  const proposed = recordProposal(initial, { text: "Public space benefits residents." });
  const critiqued = recordCritique(proposed, { accepted: true, critique: "Persuasive." });
  const judged = recordVerdict(critiqued, { verdict: "Affirmed", reason: "Public benefit." });
  const { debate: graph } = await graphs();

  assert.equal(proposed.round, 1);
  assert.equal(critiqued.critiqueAccepted, true);
  assert.deepEqual(judged.verdict, { value: "Affirmed", reason: "Public benefit." });
  assert.deepEqual(graph.nodes, [
    "open",
    "proposer",
    "critic",
    "judge",
    "complete",
    "debate-failed",
  ]);
  assert.equal(graph.routes, 8);
});
