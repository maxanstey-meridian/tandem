# 03: Operator Experience

## Outcome

This slice turns the durable configured pipeline into an application an operator
can leave running, inspect, answer, resume, and publish from.

The complete journey is:

```text
tandem run packet.md
-> terminal dashboard opens
-> pipeline and agent activity stream live
-> operator may detach and reattach
-> human questions suspend durably and appear in the dashboard
-> operator answers and the same run continues
-> accepted candidate reaches Ready
-> operator publishes it as a local branch
```

Publishing creates a branch in the packet's source repository at the exact
accepted candidate commit. It does not checkout, merge, rebase, push to a remote,
or alter the source working tree.

## Starting Point

Begin from the passing durable and real-model proofs in
`02-configured-pipeline.md`.

Retain the existing pipeline message, block implementations, `simple-v1`
composition, Durable Task host, lifecycle MCP behavior, model profiles, and Git
workspace. This slice adds operator surfaces around them.

The workflow remains authoritative. Dashboard state and local run files are
projections used for presentation and reattachment.

## Commands

Expose three operator commands.

### Start A Run

```text
tandem run <packet-path>
```

This command:

1. Loads the packet and configuration.
2. Starts the local Durable Task worker host.
3. Submits a new `simple-v1` run.
4. Opens the dashboard attached to that run.

### Attach To A Run

```text
tandem attach <run-id>
```

This command:

1. Loads the local run projection.
2. Starts the same Durable Task worker host and workflow registration.
3. Queries the existing orchestration through `DurableTaskClient` using the
   durable run ID.
4. Replays recorded Tandem events into the dashboard.
5. Tails new projected events and displays pending human requests.

If the previous Tandem process stopped, MAF Durable Task resumes the existing
workflow when this host reconnects. Completed blocks remain completed.

### Publish A Candidate

```text
tandem publish <run-id> [--branch <branch-name>]
```

This command is available only when the run is `Ready`. It publishes the exact
accepted candidate as a local branch and prints the resulting branch name and
commit SHA.

The dashboard offers the same action interactively once a run is ready.

## Local Run Projection

Keep operator-facing data under the run directory:

```text
$TANDEM_HOME/
  runs/
    <run-id>/
      run.json
      events.jsonl
      lifecycle/
      workspace/
```

`run.json` is a small latest-state projection:

```text
RunId
DurableRunId
PacketPath
RepositoryPath
Status
ActiveBlockId
PinnedBaseSha
CandidateSha
WorkspacePath
PendingHumanRequest
PublishedBranch
StartedAt
UpdatedAt
```

Update it after observable block, request, terminal, and publication events.
Write a replacement file and rename it over the previous version so interruption
cannot leave half-written JSON.

`events.jsonl` is the ordered presentation history. Append one JSON object per
line and flush each event. The dashboard may rebuild its view entirely from this
file and then follow appended lines.

Neither file decides workflow routing. On disagreement, Durable Task workflow
state wins and the projection is refreshed from the durable run.

## Run Events

Project MAF workflow and Harness updates into one small event contract:

```text
RunEvent
  EventId
  Timestamp
  RunId
  BlockId
  Kind
  Message
  Data
```

Use these event kinds:

```text
run.started
run.resumed
run.ready
run.failed
run.published
block.started
block.completed
agent.reasoning
agent.text
tool.started
tool.completed
command.output
human.requested
human.answered
```

`BlockId` is the configured block identity. There is no planner/executor/reviewer
role field in the event contract.

Derive `EventId` from the stable block invocation ID, event kind, and the event's
sequence within that invocation. Durable activity retries may append the same
event again; the dashboard and replay projection collapse duplicate event IDs.

Project events as follows:

- MAF executor start/completion becomes `block.started`/`block.completed`.
- `TextReasoningContent` becomes `agent.reasoning`.
- `TextContent` becomes `agent.text`.
- `FunctionCallContent` becomes `tool.started`.
- `FunctionResultContent` becomes `tool.completed`.
- Verification stdout and stderr become `command.output`.
- The human-question block emits `human.requested` before entering its request
  port.
- An accepted Durable Task `HumanInput` event becomes `human.answered`.
- Workflow output becomes `run.ready`, `run.failed`, or the current waiting
  state.

Write each event at its source: the block wrapper writes block events, the agent
block writes Harness updates, the verification block writes command output, and
the human blocks write request/answer events. Append the event to `events.jsonl`
and also emit it through `IWorkflowContext.AddEventAsync` so MAF and the local
projection observe the same event. The dashboard tails the local file and does
not require a live workflow stream handle.

Keep complete machine payloads in `Data` only when the dashboard needs them.
Human-readable text belongs in `Message`.

## Durable Human Input

Replace plan 02's terminal waiting block with the MAF request/response mechanism.

Use one typed request port:

```text
RequestPort.Create<HumanQuestion, HumanAnswer>("HumanInput")
```

The values are:

```text
HumanQuestion
  SourceBlockId
  Question
  Reason

HumanAnswer
  Text
```

### Preserve Pipeline Context

A request port forwards the human response, not the original pipeline message.
Preserve that message using MAF workflow shared state.

Add a human-question block before the request port. It:

1. Receives the current `PipelineMessage`.
2. Extracts `HumanQuestion` from the latest planner or reviewer outcome.
3. Writes the complete pipeline message to workflow shared state using the run ID
   as the key and `HumanInput` as the scope.
4. Returns the typed question to the request port.

Add an apply-human-answer block after the request port. It:

1. Receives `HumanAnswer`.
2. Reads the saved pipeline message from the `HumanInput` scope.
3. Adds the answer to pipeline context.
4. Emits `human.answered` with the original source block ID.

Route the updated pipeline message back to the source decision block:

```text
question from planner  -> planner
question from reviewer -> reviewer
default                -> failed
```

The planner or reviewer receives the human answer in its next prompt and returns
a new structured decision. The human response itself does not bypass those
blocks or grant mutation authority.

### Update `simple-v1`

Replace these plan 02 routes:

```text
planner.needs_human  -> terminal waiting
review.needs_human   -> terminal waiting
```

with:

```text
planner.needs_human -> human-question
review.needs_human  -> human-question
human-question      -> HumanInput request port
HumanInput response -> apply-human-answer
apply from planner  -> planner
apply from reviewer -> reviewer
```

The human-question block writes `PendingHumanRequest` to `run.json` before its
question enters the request port. The request port then suspends the durable
workflow. Pending requests also remain part of MAF's durable workflow state.

### Submit The Answer

The durable `IWorkflowClient` API starts workflows but does not provide a public
attach-existing-stream API in MAF 1.16. Use the official `DurableTaskClient`
already registered by the Durable Task host for status and responses to an
existing run.

After the operator enters text, serialize `HumanAnswer` with camel-case property
names and case-insensitive deserialization, matching MAF durable workflow user
payloads, then raise the request-port event:

```text
DurableTaskClient.RaiseEventAsync(
    durableRunId,
    "HumanInput",
    serializedHumanAnswer)
```

This is the same Durable Task event mechanism used by MAF's
`IStreamingWorkflowRun.SendResponseAsync`. The request-port ID provides framework
correlation; Tandem does not add another request protocol.

Append `human.answered` after `RaiseEventAsync` succeeds. The
apply-human-answer block clears `PendingHumanRequest` after consuming the
response. Reject an empty answer locally and keep the request pending.

## Dashboard

Use Spectre.Console and its alternate-screen support. The dashboard is a view of
the event projection and current durable status, not a controller for pipeline
routing.

### Layout

Use one adaptive layout with three areas.

```text
Header
  run ID | status | active block | model | elapsed time | context usage

Work
  streamed reasoning, text, tool activity, and command output for the active
  and recently completed blocks

Pipeline
  ordered block history, verification results, review result, human request,
  candidate SHA, and workspace or published branch

Footer
  available keys and current action
```

On a narrow terminal stack Work above Pipeline. On a wider terminal show Work
and Pipeline side by side.

The dashboard derives labels from configured block IDs and event text. It has no
fixed planner or executor pane.

Context usage comes from the active block's persisted `AgentUsage` and is shown
as current tokens, configured window, and percentage. External operations show
their elapsed duration while active; completed block summaries retain their
duration. A running block with no current external operation remains visibly
identifiable rather than appearing frozen.

### Event Rendering

Render streams incrementally:

- append reasoning and assistant text to the active block transcript;
- show each tool call on one line and replace that line when it completes;
- show verification output in a bounded scrollback area;
- retain completed block summaries in Pipeline;
- wrap text at the current panel width;
- redraw on terminal resize.

Machine lifecycle receipts and structured planner/reviewer JSON appear only as
their human-readable projected summaries.

Keep enough event history to understand the run. The JSONL file retains the full
presentation history, so the in-memory view may bound old transcript lines.

### Input

The dashboard accepts input only when an operator action is available:

```text
q          detach from the run
Enter      submit the displayed human answer
p          publish a Ready run
Ctrl+C     detach cleanly
```

While a human question is pending, show the question, reason, and originating
block above a multiline answer field. The pipeline remains suspended until MAF
accepts a non-empty response.

Detaching closes the dashboard without cancelling the durable run. In this MVP,
the local worker lives in the attached Tandem process; detaching may pause local
execution until `tandem attach` starts the worker again. Durable Task retains the
run meanwhile.

## Attach And Restart

An attached process performs this sequence:

1. Load `run.json` and prior `events.jsonl` entries.
2. Rebuild and register the same `simple-v1` workflow with the same stable block
   and request-port IDs.
3. Start the Durable Task worker and client.
4. Call `DurableTaskClient.GetInstanceAsync` with `DurableRunId` and refresh the
   orchestration status.
5. Open the dashboard using replayed events and the pending request recorded in
   `run.json`.
6. Tail `events.jsonl` and refresh durable status until terminal state or detach.

The attach path does not reconstruct workflow state from JSONL. It reconnects to
the Durable Task instance and uses JSONL only to restore presentation history.

## Publication

Publication is an explicit operator action after `Ready`. The accepted
`CandidateSha` from durable pipeline output is the only commit eligible for
publication.

### Branch Name

When `--branch` is absent, derive:

```text
tandem/<slugified-packet-title>-<first-8-run-id-characters>
```

The slug uses lowercase ASCII letters, digits, and hyphens. Validate the final
name with:

```text
git check-ref-format --branch <branch-name>
```

An explicit `--branch` follows the same validation.

### Preconditions

Before publishing, require:

```text
run status is Ready
candidate SHA is present
workspace HEAD contains the candidate commit
source repository still resolves the pinned base SHA
target local branch does not already exist
```

Read and retain the source repository's current branch or detached HEAD and its
working-tree status for the postcondition check.

### Transfer The Commit

Push directly from the isolated workspace to the source repository path:

```text
git -C <workspace> push \
  <source-repository> \
  <candidate-sha>:refs/heads/<branch-name>
```

This transfers the candidate's objects and creates only the named local branch.
There is no configured remote involved.

Verify:

```text
git -C <source-repository> rev-parse refs/heads/<branch-name>
```

The result must equal `CandidateSha`. Also verify that the source repository's
checked-out branch or detached HEAD and working-tree status are unchanged.

If the target branch already exists or Git rejects the update, leave it unchanged
and report the error. Publication never force-updates a branch.

After successful verification, update the run projection with
`PublishedBranch`, set presentation status to `Published`, append
`run.published`, and print:

```text
Published: <branch-name>
Commit:    <candidate-sha>
Repository:<source-repository>
```

The durable workflow remains completed as `Ready`; `Published` is an operator
projection over that accepted result.

## Automated Proof

### Human Suspension And Resume

Use deterministic planner and reviewer blocks.

1. Make the planner emit `planner.needs_human`.
2. Verify the workflow reaches the request port, records one pending question,
   and remains durably incomplete.
3. Stop the Tandem host.
4. Start it again and attach to the same durable run.
5. Verify the dashboard displays the same pending question from the local
   projection and Durable Task reports the same existing orchestration.
6. Send `HumanAnswer("Use the repository's existing pattern")` through the
   `HumanInput` Durable Task event.
7. Verify the planner receives the answer and the pipeline continues.
8. Verify completed pre-request blocks are not repeated.

Repeat the routing proof for a reviewer-originated question.

### Event Projection And Dashboard

Feed a representative event sequence into the dashboard view model:

```text
run started
executor reasoning delta
file tool start and completion
planner decision
command output and completion
review accepted
run ready
```

Verify the rendered model contains the active block transcript, completed block
history, context usage, operation durations, verification result, candidate SHA,
and final status. Replay the same JSONL into a new view model and verify it
produces the same visible state.

Exercise one narrow and one wide terminal size. This is a presentation test, not
a pixel snapshot suite.

### Publication

Create a temporary source repository and an isolated clone. Add a candidate
commit only to the clone, mark the run projection `Ready`, and publish.

Verify:

```text
the source repository gains the requested local branch
the branch resolves to the exact candidate SHA
the source current branch or detached HEAD is unchanged
the source working-tree status is unchanged
no remote receives a push
publishing to an existing branch fails without changing it
```

Use the real Git executable.

## Complete Real Journey

Run Tandem against a small real repository and a packet with one implementation
outcome and one verification command.

The proof must exercise:

```text
start from packet
prepare isolated workspace
executor asks planner
planner requests one human answer
operator answers through the dashboard
planner proceeds
executor edits and submits report
candidate commit is captured
verification passes
reviewer accepts
dashboard reaches Ready
operator publishes
source repository gains the accepted local branch
```

During the run, detach once and reattach with `tandem attach <run-id>`. The same
durable run must continue with its prior block outcomes and dashboard history.

The MVP is complete when the published branch points to the reviewed candidate,
the source checkout remains untouched, and the user can understand the complete
journey from the terminal dashboard.

## Slice Boundary

Stop at the local published branch. Remote push, pull requests, web UI, multi-user
hosting, distributed workers, notification systems, and additional pipeline
presets are later product work.
