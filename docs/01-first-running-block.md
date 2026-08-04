# 01: First Running Block

## Outcome

This slice produces the smallest real Tandem application.

A user runs:

```text
tandem run packet.md
```

Tandem reads the packet, pins its Git base, creates an isolated workspace, and
runs one MAF Harness agent inside one MAF Workflow block. The agent can inspect
and edit the workspace with Harness file tools. Tandem streams the work to the
terminal and reports where the edited workspace lives.

The source repository remains unchanged.

This slice proves the complete technical path once before the conditional
pipeline is added:

```text
packet
-> pinned Git workspace
-> MAF Workflow
-> agent block
-> MAF Harness
-> configured model
-> Harness file tools
-> edited workspace
-> terminal result
```

## Application Shape

Build a .NET 10 console application using one production project and one test
project. Within the production project use the simple
Domain/Application/Infrastructure/Interfaces folders from Meridian.

The boundaries are:

- Domain holds the packet and the small values that describe this run.
- Application coordinates the first-running-block journey.
- Infrastructure connects MAF, model providers, Git, processes, and local files.
- Interfaces contains the CLI and packet reader.

Keep those boundaries inside the two projects. This slice needs no additional
assemblies, modules, host process, worker process, web API, or database.

## Dependencies

Target `net10.0` with nullable reference types and warnings as errors.

Use these framework packages as the starting versions:

```text
Microsoft.Agents.AI.Harness   1.16.0
Microsoft.Agents.AI.Workflows 1.16.0
Microsoft.Extensions.AI.OpenAI 10.8.3
OpenAI                         2.10.0
System.CommandLine             2.0.0
YamlDotNet                     16.3.0
```

The MAF Harness file-access APIs are marked experimental in 1.16.0. Suppress the
corresponding MAF diagnostic only where those APIs are configured. Keep normal
compiler and analyzer warnings enabled.

MAF owns workflow execution, the agent loop, function invocation, chat history
within the turn, and file tools. Tandem supplies configuration, the block prompt,
the workflow executor, Git preparation, and CLI presentation.

## Configuration

Resolve Tandem's home directory in this order:

1. `TANDEM_HOME` when set.
2. The platform-local application data directory plus `Tandem`.

Load configuration from `$TANDEM_HOME/config.json`.

The first slice supports OpenAI-compatible providers. That includes OpenRouter,
the OpenAI API, and local or subscription-backed proxies exposing compatible
Chat Completions or Responses endpoints.

Use this configuration shape:

```json
{
  "providers": {
    "openrouter": {
      "type": "openai",
      "baseUrl": "https://openrouter.ai/api/v1",
      "apiKeyEnvironmentVariable": "OPENROUTER_API_KEY",
      "wireApi": "completions"
    },
    "chatgpt": {
      "type": "openai",
      "baseUrl": "http://127.0.0.1:10531/v1",
      "wireApi": "completions"
    }
  },
  "profiles": {
    "implementation": {
      "provider": "openrouter",
      "model": "anthropic/claude-sonnet-4.5",
      "reasoningEffort": "medium",
      "contextWindowTokens": 200000,
      "maxOutputTokens": 32000,
      "checkpointAtPercent": 80
    }
  }
}
```

The first block uses the profile named `implementation`. This is a temporary
single-block composition choice, not a fixed runtime role. The configured
pipeline introduced in the next slice will supply profile names to its blocks.

Resolve the profile as follows:

1. Find `profiles.implementation`.
2. Find the provider named by its `provider` property.
3. Read the API key from the provider's named environment variable when one is
   configured.
4. Validate that `baseUrl` is absolute and `model` is non-empty.
5. Accept `wireApi` values `completions` and `responses`.
6. Accept `reasoningEffort` values `low`, `medium`, and `high` when present.
7. Require positive `contextWindowTokens` and `maxOutputTokens` values with the
   output limit below the context-window limit.
8. Require `checkpointAtPercent` between 50 and 95.

Never put resolved API-key values into exceptions, logs, run output, or durable
run data.

### Build The Chat Client

For an OpenAI-compatible provider, create `OpenAIClient` with an
`OpenAIClientOptions.Endpoint` set to the configured `baseUrl`.

Use the configured wire API:

```text
completions -> OpenAIClient.GetChatClient(model).AsIChatClient()
responses   -> OpenAIClient.GetResponsesClient().AsIChatClient(model)
```

When the provider has an API-key environment variable, pass its value through
`ApiKeyCredential`. For a local proxy that performs authentication outside the
OpenAI protocol, provide the SDK a non-secret placeholder credential.

Apply configured reasoning effort through the `IChatClient` options pipeline:

```text
chatClient.AsBuilder()
    .ConfigureOptions(options => options.Reasoning = new()
    {
        Effort = configured effort
    })
    .Build()
```

Use the actual `ReasoningEffort` enum values supplied by
`Microsoft.Extensions.AI`; do not pass the configuration string as an untyped
provider option.

## Packet

Keep the existing Markdown packet format. A complete packet for this slice is:

```markdown
---
title: "Add a greeting"
repository: "/absolute/path/to/example-repository"
base: "main"
outcomes:
  - id: "greeting"
    description: "Create greeting.txt containing Hello from Tandem."
verification:
  - "test -f greeting.txt"
constraints:
  - "Do not change existing files."
---

Inspect the repository before making the requested change.
```

Parse the YAML frontmatter and Markdown body into:

```text
Packet
  Title
  Repository
  Base
  Outcomes[]
    Id
    Description
  Verification[]
  Constraints[]
  ImplementationContext
```

For this slice validate only what the run requires:

- the packet starts and ends its YAML frontmatter correctly;
- title, repository, base, and at least one outcome are present;
- repository is an absolute path to an existing directory;
- outcome IDs are non-empty and unique;
- verification and constraints default to empty collections;
- the Markdown body may be empty.

Packet verification commands are parsed but not executed until the configured
pipeline slice.

## Run Identity And Paths

Create a UUIDv7 run ID after the packet and configuration have loaded
successfully.

Use this local layout:

```text
$TANDEM_HOME/
  runs/
    <run-id>/
      workspace/
```

The run context passed into the workflow contains:

```text
run ID
packet
pinned commit SHA
workspace path
resolved implementation profile
```

There is no separate run-state model in this slice. The context above is the
workflow input.

## Git Workspace

Use Git as a child process and pass each argument separately rather than
constructing a shell command string.

Apply a two-minute timeout to each Git process and propagate cancellation to the
child process.

### Pin The Base

Run this in the source repository:

```text
git rev-parse --verify <packet-base>^{commit}
```

Trim stdout and require a 40- or 64-character hexadecimal commit SHA. Report a
clear packet/base error when Git exits non-zero or returns another shape.

### Create The Clone

Create the parent run directory, then execute:

```text
git clone --no-local --no-checkout <source-repository> <workspace>
git -C <workspace> checkout --detach <pinned-sha>
git -C <workspace> remote remove origin
```

`--no-local` prevents Git from sharing local repository objects through hard
links. Detached checkout makes the pinned commit explicit. Removing `origin`
prevents ordinary push or fetch operations from reaching the source repository.

If any command fails:

1. Capture its exit code and bounded stderr.
2. Delete the incomplete run directory.
3. Print one direct error identifying the failed Git operation.
4. Exit non-zero without starting the workflow.

After checkout, verify:

```text
git -C <workspace> rev-parse HEAD
```

The result must equal the pinned SHA. This is the complete Git boundary for the
first slice.

## Agent Block

Represent the operation as one MAF Workflow executor accepting the run context
and returning a block result. The block result contains:

```text
final assistant response
model ID
workspace path
```

The executor creates the configured chat client and one `HarnessAgent` for the
run.

### Harness Configuration

Root Harness file access at the workspace with:

```text
FileSystemAgentFileStore
AgentFileAccess.ReadWrite
```

Set `HarnessAgentOptions.FileAccessStore` to that store. The resulting Harness
tools provide the required operations:

```text
file_access_read
file_access_ls
file_access_grep
file_access_write
file_access_delete
file_access_replace
file_access_replace_lines
```

Wrap the filesystem store with one workspace policy: `.git` is not agent-visible
or agent-writable. Reject a path whose normalized relative segments contain
`.git`, filter `.git` from directory listings, and exclude it from recursive
grep. Delegate all other behavior to `FileSystemAgentFileStore`; retain its
absolute-path, traversal, and symlink protections.

This wrapper exists to preserve the Git repository as Tandem-owned workflow
state. It is not a second general filesystem abstraction.

Configure the Harness agent explicitly:

```text
Id                                  = "implementation"
Name                                = "Implementation"
HarnessInstructions                 = ""
DisableFileMemory                   = true
DisableTodoProvider                 = true
DisableAgentModeProvider            = true
DisableAgentSkillsProvider          = true
DisableWebSearch                    = true
DisableToolAutoApproval             = true
DisableOpenTelemetry                = true
DisableCompaction                   = true
MaximumIterationsPerRequest         = 100
```

Leave `BackgroundAgents` unset and do not add shell or MCP tools. File tools are
ordinary non-approval tools, so this slice needs no interactive approval flow.

Compaction and durable sessions belong to the configured pipeline slice. The
first run uses one in-memory `AgentSession` created immediately before the agent
turn.

### Agent Instructions

Give the Harness agent this system-level behavior:

```text
You are the implementation block in Tandem.

Work only inside the provided workspace using the available file tools.
Inspect relevant files before editing. Implement the packet outcomes while
respecting its constraints. Do not use prose as a substitute for making the
requested changes.

When the work is complete, briefly state what changed. This first slice has no
planner, reviewer, verification block, shell tool, or lifecycle MCP tools.
```

Send one user message built from the run context:

```text
Packet: <title>
Workspace: <workspace path>
Pinned base: <commit SHA>

Outcomes:
- [<id>] <description>

Constraints:
- <constraint>

Implementation context:
<Markdown body, or "(none)">

Inspect the workspace and implement the outcomes now.
```

The workspace path identifies the root for the agent, but Harness confinement is
provided mechanically by `FileSystemAgentFileStore` rather than by this prompt.

### Stream The Turn

Create an `AgentSession`, call `HarnessAgent.RunStreamingAsync`, and retain the
returned `AgentResponseUpdate` values for final aggregation.

Apply a ten-minute timeout to the model turn and propagate cancellation through
Harness and the configured chat client. Report timeout as an agent-block failure
with elapsed duration.

For every update:

1. Emit a small custom MAF workflow event carrying the update.
2. Let the CLI render `TextReasoningContent` as reasoning text.
3. Let the CLI render `TextContent` as assistant text.
4. Render `FunctionCallContent` as a tool start with its tool name.
5. Render `FunctionResultContent` as tool completion or failure.

After streaming completes, use MAF's response aggregation extension
(`ToAgentResponse`/`ToAgentResponseAsync`, according to the resulting collection
type) to obtain the final `AgentResponse`. Return its text in the block result.

## Workflow

The CLI invokes the agent through a real MAF Workflow even though this slice has
only one block.

Build it using the MAF imperative workflow API:

```text
implementationBlock = executor described above

workflow = new WorkflowBuilder(implementationBlock)
    .WithOutputFrom(implementationBlock)
    .Build()
```

Start it with:

```text
InProcessExecution.RunStreamingAsync(workflow, runContext)
```

Watch `StreamingRun.WatchStreamAsync()` until completion.

Handle these event families:

- the custom agent-update event for live output;
- `WorkflowOutputEvent` for the final block result;
- `ExecutorFailedEvent` for block failure;
- `WorkflowErrorEvent` for workflow failure.

An executor or workflow failure makes the command fail. A completed workflow
must produce exactly one block result.

This block boundary is retained by later slices. They add blocks and edges
around it rather than replacing it with a direct Harness call.

## CLI Behavior

Expose one command:

```text
tandem run <packet-path>
```

Before the model starts, print:

```text
Run:       <run-id>
Base:      <pinned-sha>
Workspace: <absolute-workspace-path>
Model:     <provider-name>/<model-id>
```

During the workflow print compact streaming lines:

```text
[reasoning] <text>
[tool] file_access_read
[tool] file_access_write
[agent] <text>
```

At completion print:

```text
Completed: <run-id>
Workspace: <absolute-workspace-path>
Result:    <final assistant response>
```

Exit codes are:

```text
0 run completed
1 packet or configuration invalid
2 Git workspace preparation failed
3 model or agent block failed
4 workflow failed or completed without one result
```

Errors go to stderr and identify the failed operation. Stack traces appear only
when a `--debug` option is supplied.

## Automated Proof

Keep the automated proof focused on this journey.

### Packet And Configuration

Parse the complete packet example above. Verify its title, repository, base,
outcome, constraint, verification command, and Markdown context.

Load a configuration containing both an OpenRouter provider and a local
OpenAI-compatible provider. Verify profile resolution and that an absent API-key
environment variable produces an error without exposing any other environment
value. Verify context-window, output-token, checkpoint threshold, and reasoning
settings are parsed into typed values.

### Git Isolation

Create a temporary source repository with one commit on `main`. Run workspace
preparation and verify:

```text
workspace HEAD equals the resolved source SHA
workspace has no remotes
workspace path belongs to the run ID
editing workspace files does not edit source files
```

Use the real Git executable in this test.

### Workflow And Harness

Provide a deterministic `IChatClient` that returns this sequence:

1. A `FunctionCallContent` invoking `file_access_write` for `greeting.txt` with
   content `Hello from Tandem.`
2. A final assistant response saying the greeting was created.

Run the real MAF Workflow and real Harness file provider against a temporary
workspace. Verify:

```text
the workflow emits tool and text updates
greeting.txt exists with the requested content
the workflow emits one block result
the result contains the final assistant response
```

Add one scripted call that attempts to write `.git/config`. Verify the workspace
policy rejects it while the ordinary `greeting.txt` write still succeeds.

This test fakes only the model boundary. It uses the real workflow, executor,
Harness loop, Harness file tool, and filesystem.

## Real Smoke Run

Create a temporary Git repository containing only a README and commit it on
`main`. Point the example packet at that repository and configure
`profiles.implementation` to either OpenRouter or another working
OpenAI-compatible endpoint.

Run:

```text
tandem run packet.md
```

The smoke run passes when:

1. The CLI shows the run ID, pinned SHA, workspace, and configured model.
2. Agent reasoning, tool calls, and final text appear while the workflow runs.
3. `greeting.txt` exists in the printed workspace with the requested content.
4. The source repository has no changed files and no new commits.
5. The process exits with code `0`.

Record total elapsed time and the duration of workspace preparation, model
requests, tool calls, and workflow execution. The handoff must identify any
period with neither a streamed event nor active external operation; a completed
run with unexplained idle time is not accepted as the performance baseline.

Record the exact command and observed workspace path in the implementation
handoff so the next slice starts from a proven run.

## Slice Boundary

Stop after the real smoke run passes. Conditional routes, planner and reviewer
blocks, lifecycle MCP tools, verification blocks, durable restart, human input,
branch publication, and the TUI are introduced by the later plans.
