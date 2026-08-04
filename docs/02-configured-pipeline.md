# 02: Configured Pipeline

## Outcome

This slice turns the working agent block into Tandem's actual product: a durable,
conditional pipeline assembled from reusable blocks.

The user still runs:

```text
tandem run packet.md
```

The run now follows the built-in `simple-v1` composition:

```text
prepare workspace
-> executor
   -> ask planner -> planner -> executor
   -> submit report -> capture candidate -> verify commands -> reviewer
   -> write checkpoint -> executor
planner needs human -> waiting
verification failed -> executor
review requested changes -> executor
review accepted -> complete
review needs human -> waiting
```

The graph above is one composition of reusable blocks. MAF Workflows owns graph
execution and routing. MAF Durable Task owns durable progress. MAF Harness owns
each agent's model and tool loop.

This slice is complete when the same block implementations can be composed into
different graphs and those composition changes alter the observed lifecycle.

## Starting Point

Begin from the passing real smoke run in `01-first-running-block.md`.

Retain from that slice:

- packet and provider/profile loading;
- OpenRouter and OpenAI-compatible model support;
- Git process execution and isolated workspaces;
- the MAF Harness configuration and file tools;
- streamed agent updates;
- the working agent executor boundary.

Change the workflow from one in-process block to a durable graph. Do not replace
the proven Harness and provider path with another agent abstraction.

## Additional Dependencies

Add the official MAF Durable Task integration and its local scheduler client and
worker packages:

```text
Microsoft.Agents.AI.DurableTask 1.16.0-preview.260730.1
Microsoft.DurableTask.Client.AzureManaged
Microsoft.DurableTask.Worker.AzureManaged
Microsoft.Extensions.Hosting
ModelContextProtocol            1.2.0
```

Pin the Durable Task preview version. The core Harness and Workflows packages
remain on `1.16.0`. Keep `Microsoft.Extensions.AI.OpenAI` at `10.8.3`; Durable
Task requires `Microsoft.Extensions.AI >= 10.7.0`, so a direct `10.6.x` reference
would create a package downgrade.

For local development run the Durable Task Scheduler emulator:

```text
docker run -d --name tandem-dts \
  -p 8080:8080 \
  -p 8082:8082 \
  mcr.microsoft.com/dts/dts-emulator:latest
```

Use this connection string unless the environment supplies another one:

```text
Endpoint=http://localhost:8080;TaskHub=tandem;Authentication=None
```

The scheduler endpoint is port `8080`; its dashboard is
`http://localhost:8082`.

## Framework Fit Gate

Prove the preview durable integration before implementing product blocks. Build
one disposable test workflow using the real MAF and DTS emulator and verify:

1. An ordered `AddSwitch` selects the first matching case.
2. A cycle can return to an earlier executor and preserve its message.
3. Stable workflow and executor IDs allow a stopped host to resume the same run.
4. Completed executors are not repeated after restart.
5. Custom workflow events are visible from durable execution.
6. A `RequestPort` suspends, survives host restart, and consumes a typed external
   response.

Then prove a Harness session can be serialized, restored, and continued after a
tool turn. Keep these as integration tests. If a framework capability fails,
resolve that fit before building `simple-v1`; do not compensate with a Tandem
workflow engine.

## Model Profiles

Extend the plan 01 configuration with the profiles referenced by `simple-v1`:

```json
{
  "profiles": {
    "implementation": {
      "provider": "openrouter",
      "model": "anthropic/claude-sonnet-4.5",
      "reasoningEffort": "medium",
      "contextWindowTokens": 200000,
      "maxOutputTokens": 32000,
      "checkpointAtPercent": 80
    },
    "planning": {
      "provider": "openrouter",
      "model": "openai/gpt-5.4",
      "reasoningEffort": "high",
      "contextWindowTokens": 400000,
      "maxOutputTokens": 32000,
      "checkpointAtPercent": 80
    },
    "review": {
      "provider": "openrouter",
      "model": "openai/gpt-5.4",
      "reasoningEffort": "high",
      "contextWindowTokens": 400000,
      "maxOutputTokens": 32000,
      "checkpointAtPercent": 80
    }
  }
}
```

These are complete examples, not required vendor/model choices. `simple-v1`
references the profile names; the user may point each profile at any configured
OpenAI-compatible provider, including OpenRouter.

## The Shared Message

All product blocks receive and return one serializable pipeline message. This
keeps MAF edges type-compatible while preserving the distinction between shared
facts and the latest block result.

```text
PipelineMessage
  Context
  LatestOutcome
```

`PipelineContext` contains only durable run facts:

```text
RunId
Packet
PinnedBaseSha
WorkspacePath
MutationAuthorized
PlannerDecision
PlannerConstraints[]
CandidateSha
VerificationIndex
VerificationResults[]
AgentSessions by block ID
AgentUsage by block ID
InvocationCounts by block ID
Status
```

Each `AgentUsage` value contains:

```text
CurrentInputTokens
CurrentOutputTokens
CurrentContextTokens
ContextWindowTokens
CheckpointAtTokens
LastModelCallDuration
```

`BlockOutcome` contains:

```text
Kind
BlockId
Summary
Payload
```

`Kind` is an ordinary stable string emitted by the block, such as
`planner.requested`, `report.submitted`, `command.passed`, or `review.accepted`.
It is not a runtime enum and does not cause behavior by itself. MAF edge
conditions decide what each kind means in a particular composition.

The payload is a JSON-serializable value owned by the block that emitted it.
Block-specific code deserializes its own payload. The runtime does not maintain a
registry of every possible outcome shape.

Every executor invocation derives a stable invocation ID from:

```text
<run-id>--<block-id>--<next invocation count for that block>
```

The block increments its invocation count only in the returned context. A
Durable Task retry therefore receives the same count and derives the same
invocation ID.

## Reusable Blocks

Implement blocks as MAF `Executor<PipelineMessage, PipelineMessage>` classes.
Each block performs its operation and returns an updated immutable context plus
one outcome. It does not name or invoke its successor.

The slice needs these block implementations.

### Prepare Workspace

Move the workspace preparation proven in plan 01 into the first workflow block.

Input requirements:

```text
packet loaded
run ID assigned
workspace not yet created
```

Operation:

```text
resolve packet.base^{commit}
clone --no-local --no-checkout
checkout --detach at the pinned SHA
remove origin
verify workspace HEAD
```

Outcome:

```text
Kind: workspace.prepared
Payload: pinned SHA and workspace path
```

The updated context contains the pinned SHA, workspace path, and status
`Running`.

### Agent Block

Generalize the plan 01 executor into a reusable configured agent block. Its
constructor receives:

```text
block ID
model profile name
system instructions
workspace access: read-only or mutation-gated
lifecycle MCP tool names
```

The executor resolves its profile, restores or creates its MAF `AgentSession`,
builds the Harness agent, sends the configured prompt, streams updates, and
returns the accepted block outcome.

Store sessions by configured block ID. Serialize the updated session with MAF's
`SerializeSessionAsync` and place that JSON value in the returned pipeline
context. Restore it with `DeserializeSessionAsync` before the next invocation of
the same block.

Consume every MAF `UsageContent` update. For the latest model request record:

```text
CurrentInputTokens  = usage input tokens
CurrentOutputTokens = usage output tokens
CurrentContextTokens = input tokens + output tokens
CheckpointAtTokens = floor(contextWindowTokens * checkpointAtPercent / 100)
```

Before starting a normal executor invocation, compare:

```text
CurrentContextTokens + MaxOutputTokens >= CheckpointAtTokens
```

When true, run the executor in checkpoint-only mode: restore its current session,
provide only `write_checkpoint`, and instruct it to emit the typed checkpoint.
Text without an accepted checkpoint tool call fails that block invocation. After
`checkpoint.written`, retain the checkpoint payload, remove the serialized
executor session and usage, and route back to executor. The next invocation
starts a fresh session with the checkpoint in its prompt.

This makes the threshold mechanical. The model writes checkpoint content; it
does not decide whether the configured threshold has been crossed.

The executor block uses Harness file access as follows:

```text
read-only       -> AgentFileAccess.ReadOnly
mutation-gated  -> ReadOnly while MutationAuthorized is false
mutation-gated  -> ReadWrite while MutationAuthorized is true
```

The configured profile and workspace policy belong to the block instance. There
is no runtime `AgentRole` enum.

### Planner Block

Configure an agent block named `planner` using the profile `planning` and
read-only workspace access.

Its prompt contains:

```text
packet outcomes and constraints
executor's question
executor's proposed approach
executor's evidence paths
previous planner constraints
```

Require a structured response matching:

```text
PlannerDecision
  Decision: Proceed | ProceedWithConstraints | NeedsHuman | Stop
  Rationale
  Constraints[]
  EvidenceUsed[]
  HumanQuestion
```

Use MAF structured output rather than parsing prose. The planner may read files
through Harness before answering.

Map the response to one of these outcomes:

```text
planner.proceed
planner.proceed_with_constraints
planner.needs_human
planner.stop
```

Proceed outcomes set `MutationAuthorized` to true and update planner constraints.
The other outcomes leave mutation authority closed.

### Capture Candidate Block

After an executor submits its implementation report, capture exactly what will
be verified and reviewed.

Run in the workspace:

```text
git add -A
git -c user.name=Tandem \
    -c user.email=tandem@localhost \
    commit --allow-empty -m "Tandem candidate <run-id>"
git rev-parse HEAD
```

Store the resulting SHA as `CandidateSha` and reset verification index and
results. Emit:

```text
candidate.captured
```

Each later remediation and report creates a new candidate commit. Previous
candidate commits remain in the isolated workspace and need no publication or
cleanup in this slice.

### Verification Block

One reusable verification block runs one configured command per invocation.
The current command is `Packet.Verification[VerificationIndex]`.

Execute it in the workspace using the platform shell:

```text
/bin/zsh -lc <command>       macOS
/bin/bash -lc <command>      Linux
cmd.exe /d /s /c <command>   Windows
```

Capture exit code, stdout, stderr, and elapsed time. Bound retained output to a
useful tail while streaming live lines as workflow events.

Return:

```text
command.passed
command.failed
```

The payload contains the command, index, exit code, elapsed time, and output
summary. A pass increments `VerificationIndex`; a failure retains the failed
index. A newly captured candidate resets the index to zero.

When the packet has no verification commands, the capture block routes directly
to review.

### Reviewer Block

Configure an agent block named `reviewer` using profile `review` and read-only
workspace access.

Before invoking it, produce:

```text
git diff --binary <pinned-base-sha>..<candidate-sha>
git diff --name-status -z <pinned-base-sha>..<candidate-sha>
```

Give the reviewer the packet, planner constraints, implementation report,
verification results, exact candidate SHA, diff, and changed-file list. The
reviewer can inspect changed files through Harness.

Require a structured response matching:

```text
ReviewDecision
  Decision: Accept | RequestChanges | NeedsHuman
  Summary
  Findings[]
    Severity
    Description
    Evidence
  HumanQuestion
```

Map it to:

```text
review.accepted
review.changes_requested
review.needs_human
```

The reviewer evaluates the candidate identified in context. It does not inspect
an implicitly moving working tree.

### Complete And Waiting Blocks

The complete block returns the accepted candidate SHA, workspace path,
verification summary, and review summary as the workflow output. It sets status
`Ready` and emits `run.ready`.

For this slice, the waiting block returns a workflow output with status
`WaitingForHuman`, the question, and the current workspace. Plan 03 replaces
this terminal representation with a durable MAF `RequestPort` and operator
response.

### Failed Block

Every switch has a default route to one failure block. It reports the source
block and unhandled outcome kind, sets status `Failed`, and returns a failed
workflow output. This makes incomplete composition visible instead of silently
ending a run.

## Lifecycle MCP Tools

The executor block uses three product lifecycle tools through the official MCP
SDK:

```text
ask_planner
submit_report
write_checkpoint
```

Normal executor invocations expose `ask_planner` and `submit_report`.
Checkpoint-only invocations expose only `write_checkpoint`.

Host them from the same Tandem executable using a hidden stdio MCP command. The
agent block starts that command with the run ID, block ID, and stable invocation
ID supplied out of band. These values are not model arguments.

Create the client with the official MCP `StdioClientTransport`, invoking the
current Tandem executable with an internal `mcp lifecycle` command. Pass run,
block, invocation, and Tandem-home values through the child environment. Reserve
child stdout exclusively for MCP protocol frames; send diagnostics to stderr and
the run event sink.

Create and dispose one MCP client for each agent-block invocation. Apply the
block cancellation token to startup, tool discovery, invocation, and disposal.
Allow 30 seconds for an individual lifecycle call. Cancellation or timeout must
terminate the stdio child and fail the block visibly rather than leaving a
process behind.

### ask_planner

Input:

```text
question
proposedApproach
evidence[]
```

Accepted outcome:

```text
Kind: planner.requested
Payload: question, proposed approach, and evidence
```

### submit_report

Input:

```text
summary
outcomes[]
evidence[]
```

Accepted outcome:

```text
Kind: report.submitted
Payload: summary, outcome claims, and evidence
```

### write_checkpoint

Input:

```text
summary
completed[]
next[]
```

Accepted outcome:

```text
Kind: checkpoint.written
Payload: summary, completed work, and next work
```

The MCP command validates required fields and allows one authoritative lifecycle
outcome per block invocation. Repeating the same accepted call returns the same
receipt. A conflicting second outcome for the same invocation is rejected.

Persist the accepted receipt before returning the MCP result:

```text
$TANDEM_HOME/runs/<run-id>/lifecycle/<invocation-id>.json
```

Write one temporary file and atomically rename it to that final path. The receipt
contains the invocation ID, block ID, outcome kind, payload, and acceptance time.
If the final file already exists, return it when the submitted kind and payload
match; reject the call when they differ. This receipt is the idempotency marker
for an authoritative external side effect, not an alternative workflow store.

Return this MCP result shape:

```json
{
  "accepted": true,
  "invocationId": "<stable invocation ID>",
  "blockId": "executor",
  "outcome": {
    "kind": "planner.requested",
    "summary": "<human-readable summary>",
    "payload": {}
  }
}
```

Before creating an MCP client, restoring an agent session, or calling the model,
the agent block checks for the invocation receipt file. When present and valid,
it returns that recorded outcome directly. This startup rule closes the crash
window between MCP acceptance and Durable Task committing the activity result.

### Mechanical Turn Termination

Attach MAF function middleware around the MCP tools.

The middleware sequence is:

```text
invoke MCP tool
-> receive accepted receipt
-> record receipt in the invocation collector
-> set FunctionInvocationContext.Terminate = true
-> return from Harness
```

Harness persists chat history after each model service call. The block serializes
the resulting session and returns the collected lifecycle outcome in its pipeline
message. MAF Durable Task then commits the block result before evaluating routes.

The block ignores assistant text emitted after the accepted lifecycle tool and
uses the collected receipt, not prose, as its outcome.

On a Durable Task retry, the block uses the same stable invocation ID. The
receipt remains the source of the authoritative lifecycle outcome; the restored
agent session remains the source of conversation history for invocations without
an accepted receipt.

Prove this behavior before adding any other lifecycle tools. If Tandem cannot
recover an accepted MCP result or MAF cannot terminate the turn consistently,
stop this slice rather than adding a second agent loop or workflow engine.

## Executor Prompt

The executor block keeps one session across its invocations. Each invocation
adds current pipeline facts to the user message:

```text
Packet outcomes and constraints
Pinned base and workspace
Mutation authority: open or closed
Latest planner decision and constraints
Latest verification failure, if any
Latest review findings, if any
Current candidate SHA, if any
```

Its system instructions establish only behavior owned by this block:

```text
You are Tandem's implementation block.

Inspect the workspace and work toward the packet outcomes. When mutation
authority is closed, use read-only tools to understand the repository and call
ask_planner with your proposed approach before editing. When authority is open,
implement the approved approach and constraints.

Call ask_planner whenever independent guidance is required. During a
checkpoint-only invocation, call write_checkpoint with the supplied work state.
When the implementation is ready for verification, call submit_report with
outcome claims and repository evidence.

An accepted lifecycle call ends the current turn. Do not represent planner,
verification, or reviewer decisions yourself.
```

Tool availability and middleware enforce mutation and termination mechanically;
the prompt explains those boundaries to the model.

When the outcome is `checkpoint.written`, retain the checkpoint payload in
pipeline context and remove the executor's serialized session. The next executor
invocation creates a fresh session and receives the checkpoint in its prompt.

## `simple-v1` Composition

Build `simple-v1` directly with MAF `WorkflowBuilder`. The composition function
instantiates configured block executors and connects them. It is not a second
workflow representation or interpreter.

Use stable block IDs:

```text
prepare
executor
planner
capture-candidate
verify
reviewer
complete
waiting
failed
```

Configure edges and switches in this order.

### Prepare

```text
prepare workspace.prepared -> executor
default                    -> failed
```

### Executor

```text
executor planner.requested  -> planner
executor report.submitted   -> capture-candidate
executor checkpoint.written -> executor
default                     -> failed
```

### Planner

```text
planner planner.proceed                  -> executor
planner planner.proceed_with_constraints -> executor
planner planner.needs_human              -> waiting
planner planner.stop                     -> failed
default                                  -> failed
```

### Candidate

```text
capture candidate.captured and commands remain -> verify
capture candidate.captured and no commands      -> reviewer
default                                          -> failed
```

### Verification

```text
verify command.passed and commands remain -> verify
verify command.passed and all complete     -> reviewer
verify command.failed                      -> executor
default                                    -> failed
```

### Review

```text
reviewer review.accepted          -> complete
reviewer review.changes_requested -> executor
reviewer review.needs_human       -> waiting
default                           -> failed
```

Use `WorkflowBuilder.AddSwitch` with ordered `AddCase` calls and `WithDefault`
for these branches. Conditions inspect only the `PipelineMessage` they receive.

Mark `complete`, `waiting`, and `failed` as workflow outputs.

## Durable Host

Keep the `simple-v1` workflow definition independent of its runner. Register the
same workflow with MAF Durable Task in a .NET Generic Host:

```text
services.ConfigureDurableWorkflows(
    workflows => workflows.AddWorkflow(simpleV1),
    workerBuilder => workerBuilder.UseDurableTaskScheduler(connectionString),
    clientBuilder => clientBuilder.UseDurableTaskScheduler(connectionString))
```

Start runs through `IWorkflowClient`:

```text
IWorkflowClient.RunAsync(simpleV1, initialPipelineMessage)
```

Await completion through `IAwaitableWorkflowRun.WaitForCompletionAsync` and
render the terminal result using the existing plain CLI output.

Use the Tandem run ID as the durable workflow run ID when the API permits one to
be supplied. Otherwise persist the mapping printed by `IWorkflowRun.RunId` in
the run directory.

The CLI process may host both the worker and client in this slice. Durable Task
Scheduler remains the durable backend. Plan 03 may separate operator attachment
from worker lifetime only where the TUI requires it.

## Timing And Timeouts

Emit start time, completion time, and elapsed duration for every block and every
external operation. Use these MVP defaults unless configuration supplies a
smaller positive value:

```text
Git command          2 minutes
model request       10 minutes
lifecycle MCP call  30 seconds
verification command 10 minutes
```

Propagate the resulting cancellation token through MAF, MCP, Git, and process
execution. A timeout produces a visible failed block outcome containing the
operation and elapsed time.

The real-model proof records total run duration and durations for every model
request, lifecycle call, Git operation, and verification command. Any interval
without a running external operation or emitted event is investigated before the
slice is accepted.

## Composition Proof

Exercise the graph with deterministic block implementations. These tests use MAF
Workflows and change only the `WorkflowBuilder` composition.

Prove:

1. A composition without a planner edge never invokes the planner block.
2. Inserting a recording block before planner makes it run before planner.
3. Two verification commands run in packet order.
4. A failed first command routes to executor and skips the second command.
5. Passing both commands routes to reviewer.
6. A composition without review completes after successful verification.
7. Two configured review blocks can run sequentially without runtime changes.
8. A custom condition can route an executor outcome containing Chinese
   characters to a second agent block configured with another model profile.
9. Usage below the configured threshold runs the normal executor invocation.
10. Usage crossing the threshold runs checkpoint-only mode, accepts one typed
    checkpoint, clears the old session, and starts the next executor invocation
    with that checkpoint.

The test block implementations may return prepared outcomes without invoking a
model. They are substitutes for block operations, not a fake workflow runtime.

## Lifecycle Proof

Use a deterministic chat client and the real Harness/MCP path.

For `ask_planner`, make the model:

1. Invoke the MCP tool with a valid proposed approach.
2. Attempt to emit another file-write tool call and assistant text afterward.

Verify:

```text
the lifecycle outcome is accepted once
the turn terminates after the MCP result
the later file write does not run
the later assistant text is not used as the block outcome
the workflow routes to planner
the stdio MCP child exits after the block completes
MCP protocol stdout never appears as a run event
```

Retry the same invocation with its receipt present, including once without a
serialized session, and verify that the outcome is recovered before invoking the
model or MCP tool again.

Cancel one invocation while its MCP call is active and verify the block fails
with the cancellation reason and leaves no child process running.

Repeat the same proof for `submit_report` routing to candidate capture.

## Durable Proof

Run against the real local Durable Task Scheduler emulator.

Use deterministic block operations so timing is controlled:

1. Start `simple-v1` and let prepare and executor complete.
2. Stop the Tandem host before the next block completes.
3. Start the host again with the same workflow definition and stable block IDs.
4. Verify the existing durable run continues and already completed blocks are
   not repeated.
5. Verify it reaches the expected terminal output.

Then run one real-model lifecycle:

```text
prepare
-> executor asks planner
-> planner proceeds
-> executor edits and submits report
-> candidate captured
-> packet verification passes
-> reviewer accepts
-> Ready
```

Inspect the workspace and Durable Task dashboard to confirm the block order,
candidate SHA, command result, and accepted review.

The slice passes only with MAF Durable Task performing the workflow recovery.

## CLI Result

Keep stdout presentation simple. In addition to the plan 01 streaming output,
print block transitions:

```text
[block] prepare completed: workspace.prepared
[block] executor completed: planner.requested
[block] planner completed: planner.proceed
[block] executor completed: report.submitted
[block] capture-candidate completed: candidate.captured
[block] verify completed: command.passed
[block] reviewer completed: review.accepted
[block] complete completed: run.ready
```

At a terminal state print:

```text
Status:       Ready | WaitingForHuman | Failed
Run:          <run-id>
Base:         <pinned-sha>
Candidate:    <candidate-sha, when present>
Workspace:    <workspace-path>
Verification: <passed count>/<total count>
Review:       <summary, when present>
Question:     <human question, when waiting>
```

## Slice Boundary

Stop when the configured durable lifecycle and its real-model proof pass. Plan
03 adds the terminal dashboard, durable human response, run attachment, context
presentation, and explicit publication of the accepted candidate as a local
branch.
