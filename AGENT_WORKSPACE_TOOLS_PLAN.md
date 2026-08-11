# Agent Workspace Tools Plan

## Status

Planned against Tandem HEAD `105069c15958fb2f221f73d8e33d10c1ce6487d3` and the
current uncommitted read-only Git implementation.

This plan replaces the current workspace mutation boolean and standalone
`WithReadOnlyGit()` switch with reusable repository environments and explicit,
per-agent tool access. It adds repository-owned fixed commands, an expanded
bounded Git bundle, and MAF-backed shell execution to both the C# and TypeScript
authoring surfaces.

## Recommendation

Implement five connected concepts:

1. `AgentWorkspace<TState>` owns a repository path and repository-specific
   command catalogue.
2. `AgentTools.Always(...)` and `AgentTools.When(...)` explicitly assign tools
   to an agent and show their availability conditions at the composition root.
3. Fixed repository commands execute through MAF's maintained shell executor
   but do not allow the model to supply command text.
4. `"git:ro"` expands to a useful bounded Git inspection bundle.
5. `"shell"` deliberately grants model-authored process execution through
   MAF's maintained shell tool.

The application owns repository commands. Individual agents receive
role-specific access to that repository environment. Tandem owns composition,
effect classification, state-dependent exposure, guards, observation, and the
adapter into maintained MAF providers.

## Intended C# API

Define a repository environment once:

```csharp
var repository = AgentWorkspace<CadenceState>.Define(
    path: state => state.WorkspacePath,
    commands: state =>
        state.Packet.Verification.Select((command, index) =>
            AgentCommand.Define(
                name: $"run_verification_{index + 1}",
                description: $"Run verification command {index + 1}: {command}",
                command: command
            )
        )
);
```

Give the Executor repository inspection, repository commands, and gated file
mutation:

```csharp
var executor = Agent
    .Create<CadenceState>(
        "executor",
        ExecutorPrompts.Instructions,
        chatClient
    )
    .UseHarness(DeliveryHarnessInstructions.Value)
    .WithWorkspace(
        repository,
        tools:
        [
            AgentTools.Always(
                "read_file",
                "ls",
                "grep",
                "git:ro",
                repository.Commands
            ),
            AgentTools.When(
                state => state.MutationAuthorized,
                "write_file",
                "delete_file",
                "replace",
                "replace_lines"
            )
        ]
    )
    .Build();
```

Give the Reviewer read access, Git inspection, and the same repository-owned
verification commands:

```csharp
reviewer.WithWorkspace(
    repository,
    tools:
    [
        AgentTools.Always(
            "read_file",
            "ls",
            "grep",
            "git:ro",
            repository.Commands
        )
    ]
);
```

An agent that deliberately receives unrestricted process execution declares it
separately:

```csharp
agent.WithWorkspace(
    repository,
    tools:
    [
        AgentTools.Always(
            "read_file",
            "ls",
            "grep",
            "git:ro"
        ),
        AgentTools.When(
            state => state.CommandExecutionAuthorized,
            "shell"
        )
    ]
);
```

A fixed repository can declare static commands:

```csharp
var repository = AgentWorkspace<ProjectState>.Define(
    path: state => state.WorkspacePath,
    commands:
    [
        AgentCommand.Define(
            "run_tests",
            "Run the complete test suite.",
            "task test"
        ),
        AgentCommand.Define(
            "run_format_check",
            "Check repository formatting.",
            "task format:check"
        )
    ]
);
```

## Intended TypeScript API

Define the same repository environment from TypeScript state:

```ts
const repository = agentWorkspace({
  path: (state: CadenceState) => state.workspacePath,
  commands: (state) =>
    state.packet.verification.map((command, index) => ({
      name: `run_verification_${index + 1}`,
      description: `Run verification command ${index + 1}: ${command}`,
      command,
    })),
});
```

Attach role-specific access to an agent:

```ts
const executor = agent({
  id: "executor",
  instructions: "...",
  client,
  message: (state) => state.message,

  workspace: repository.withTools([
    agentTools.always(
      "read_file",
      "ls",
      "grep",
      "git:ro",
      repository.commands,
    ),
    agentTools.when(
      (state) => state.mutationAuthorized,
      "write_file",
      "delete_file",
      "replace",
      "replace_lines",
    ),
  ]),
});
```

Unrestricted shell remains visibly separate:

```ts
workspace: repository.withTools([
  agentTools.always(
    "read_file",
    "ls",
    "grep",
    "git:ro",
  ),
  agentTools.when(
    (state) => state.commandExecutionAuthorized,
    "shell",
  ),
]),
```

## Boundary And Ownership

The ownership model is:

- `AgentWorkspace<TState>` owns the repository path and repository-specific
  commands.
- Agent tool groups own which facilities a role receives and when they are
  exposed.
- Tandem maps authored selections to MAF and bounded framework tools.
- MAF owns filesystem behavior and shell execution mechanics.
- The application remains responsible for deciding which agents receive local
  process authority.
- Pipeline-owned verification remains the authoritative acceptance gate.

Repository commands are not capabilities. They do not conclude the agent visit,
apply `TState`, or represent lifecycle transitions. They are utility tools whose
stdout, stderr, exit code, duration, timeout, and truncation return directly to
the model inside the current agent loop.

This preserves Tandem's core invariants:

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

Repository command definitions are application composition. Process creation,
shell selection, cancellation, output capture, and termination remain below the
seam in MAF.

## Existing Tandem Concepts

### Advanced workspace authoring

`src/Tandem.Advanced/AdvancedPipeline.cs` currently exposes:

```csharp
WithWorkspace(
    Func<TState, string> path,
    Func<TState, bool> allowMutation,
    ToolInterceptor<TState>? toolInterceptor = null
)
```

It also contains the current uncommitted:

```csharp
WithReadOnlyGit()
```

The current workspace API couples three separate decisions:

- workspace identity;
- file-tool selection;
- whether all mutation tools are exposed.

The replacement makes workspace identity reusable and tool availability
explicit.

### Core builder configuration

`src/Tandem/Authoring/AgentSdk.cs` currently stores:

- `_workspacePath`;
- `_allowMutation`;
- `_exposeReadOnlyGitTools`;
- `_toolInterceptor`.

`AgentBuilder<TState>.Build()` carries those into
`src/Tandem/Domain/AgentBlockConfig.cs` as:

```csharp
Func<TState, string>? WorkspacePath
Func<TState, bool>? AllowMutation
bool ExposeReadOnlyGitTools
```

These should become one immutable workspace descriptor rather than accumulating
additional feature booleans.

### Runtime implementation seam

`src/Tandem/Infrastructure/AgentImplementation.cs` currently carries resolved
workspace state through `AgentImplementationContext`:

```csharp
string? WorkspacePath
bool ExposeWorkspaceMutationTools
bool ExposeReadOnlyGitTools
```

This should become one resolved workspace tool plan containing only facilities
authorized for the current agent visit.

`ToolEffectRegistry` already gives Tandem one authoritative classification per
tool name. New file, Git, command, and shell tools must register there before
gated-agent validation.

### Agent visit resolution

`src/Tandem/Infrastructure/Blocks/AgentBlock.cs` constructs the MAF agent in
`CreateAgent`. It evaluates `AllowMutation` against current state and reconstructs
the live MAF agent during initial execution, continuation, correction, and
checkpoint transitions.

Workspace path, command catalogue, and tool-group predicates must therefore be
resolved in `CreateAgent` for every visit/agent reconstruction. This naturally
ensures updated state changes the exposed tool schema on the next visit.

### Harness implementation

`src/Tandem.Advanced/HarnessAgentImplementation.cs` currently:

- builds a `GitExcludedFileStore`;
- installs MAF `FileAccessProvider` through `HarnessAgentOptions`;
- selects all read tools and optionally all write tools;
- registers effects through `HarnessToolEffects`;
- adds the current custom read-only Git tools when enabled.

MAF's `FileAccessProviderOptions` only toggles the complete write group. It does
not allow individual selection of `read_file`, `ls`, `grep`, or individual write
tools. Tandem needs a thin filtering `AIContextProvider` decorator around the
maintained MAF provider to honor explicit selections. It must not reimplement
filesystem tools.

### Current read-only Git implementation

The dirty worktree contains:

- `src/Tandem.Advanced/ReadOnlyGitTools.cs`;
- `tests/Tandem.Tests/Infrastructure/ReadOnlyGitToolsTests.cs`;
- builder/runtime wiring and Delivery policy regression changes.

The current implementation exposes only:

- `git_changed_files`;
- `git_diff` for exact base/candidate comparison.

It directly launches `git` with `UseShellExecute = false` and `ArgumentList`,
validates revisions and paths, and bounds output. This is a reasonable bounded
Git adapter, but its current API is too narrow and `WithReadOnlyGit()` hides the
actual tool surface.

### TypeScript authoring and bridge

`typescript/packages/sdk/src/index.ts` currently has no workspace or harness
authoring surface. Agent declarations carry instructions, client, message,
output, capabilities, skills, continuation, timeout, and persistence.

`typescript/bridge/RegistrationContract.cs` is currently registration contract
version 7. `RegisteredNodeContract` has no workspace field.

`typescript/bridge/RegisteredParticipants.cs` builds ordinary Core agents. To
support workspace tools, the bridge must reference `Tandem.Advanced`, apply
`UseHarness`, and translate the registration contract through the same public C#
workspace API. There must not be a TypeScript-specific shell or Git runtime.

## Public C# Contracts

### `AgentWorkspace<TState>`

Add to `Tandem.Advanced`:

```csharp
public sealed class AgentWorkspace<TState>
{
    public static AgentWorkspace<TState> Define(
        Func<TState, string> path,
        IReadOnlyList<AgentCommand> commands
    );

    public static AgentWorkspace<TState> Define(
        Func<TState, string> path,
        Func<TState, IReadOnlyList<AgentCommand>> commands
    );

    public AgentToolSelection Commands { get; }
}
```

The static overload stores a constant command catalogue. The dynamic overload
resolves commands from current state at the start of each agent visit.

### `AgentCommand`

```csharp
public sealed record AgentCommand
{
    public static AgentCommand Define(
        string name,
        string description,
        string command
    );
}
```

Validation:

- name must be a valid function-tool name;
- description must be nonblank;
- command must be nonblank;
- duplicate names fail;
- collisions with built-ins, capabilities, skill tools, and other commands
  fail.

The command text is never model input.

### Tool selections and groups

```csharp
public static class AgentTools
{
    public static AgentToolGroup<TState> Always<TState>(
        params AgentToolSelection[] tools
    );

    public static AgentToolGroup<TState> When<TState>(
        Func<TState, bool> predicate,
        params AgentToolSelection[] tools
    );
}
```

`AgentToolSelection` supports:

- a validated built-in tool name;
- the `"git:ro"` bundle marker;
- the workspace's opaque repository-command marker.

Unknown names fail during authoring. Duplicate effective tools across groups fail
rather than relying on group order.

## Initial Tool Catalogue

The first supported names are:

```text
read_file
ls
grep

write_file
delete_file
replace
replace_lines

git:ro
shell
```

`"git:ro"` is a bundle marker and is not model-visible. It expands to:

```text
git_status
git_diff
git_log
git_show
git_blame
git_changed_files
git_compare
```

Repository commands become their declared tool names, such as:

```text
run_tests
run_format_check
run_verification_1
```

## Tool Availability

Tool groups control exposure, not post-call approval.

When this predicate is false:

```csharp
AgentTools.When(
    state => state.MutationAuthorized,
    "write_file"
)
```

`write_file` is absent from the model's tool schema for that visit.

Predicates are reevaluated whenever the pipeline revisits or reconstructs the
agent with updated state.

This remains separate from `AgentStateGuard<TState>` and latched gates:

- tool groups control potential availability for the visit;
- state guards intercept exposed tools based on effects;
- latched gates can block effects after becoming active during the same visit.

## Authority Classification

Extend public and internal `ToolEffect` with:

```csharp
ProcessExecution
```

Classifications:

```text
read_file, ls, grep              -> Read
git:*                            -> Read
write/delete/replace             -> WorkspaceMutation
fixed repository command         -> ProcessExecution
shell                            -> ProcessExecution
capability                       -> LifecycleTransition
```

Fixed commands remain `ProcessExecution` even when named `run_tests`. Test suites
can generate files, invoke subprocesses, access networks, or mutate caches.

Existing guards blocking only `WorkspaceMutation` continue allowing tests. An
application can explicitly block `ProcessExecution` independently.

## MAF File Tools

Continue using:

- MAF `FileAccessProvider`;
- MAF `AgentFileStore`;
- Tandem's `BomlessFileSystemAgentFileStore`;
- Tandem's `GitExcludedFileStore`.

Add a filtering context-provider decorator that:

1. delegates context creation and invocation to MAF;
2. retains only selected MAF tool names;
3. preserves MAF instructions and session state;
4. does not duplicate read, list, grep, write, delete, or replace logic.

`.git` remains inaccessible through general file tools.

## MAF Shell Package

Use the official published package:

```xml
<PackageReference
    Include="Microsoft.Agents.AI.Tools.Shell"
    Version="1.16.0-preview.260730.1" />
```

The package is published on NuGet and depends on:

```text
Microsoft.Agents.AI.Abstractions 1.16.0
```

It aligns with Tandem's current MAF 1.16 package line without requiring an
upgrade of the stable Harness package.

MAF supplies:

- OS shell resolution;
- stateless and persistent shell modes;
- working-directory handling;
- stdout/stderr and exit codes;
- timeout and cancellation;
- process-tree termination;
- output truncation;
- environment configuration;
- shell policy;
- approval wrapping;
- `LocalShellExecutor` and `DockerShellExecutor`.

Tandem must not add its own `ProcessStartInfo`, shell selection, quoting,
process-kill, timeout, or output-capture implementation for command tools.

## Fixed Repository Commands

Use MAF `LocalShellExecutor` in stateless mode:

```csharp
var shell = new LocalShellExecutor(
    new LocalShellExecutorOptions
    {
        Mode = ShellMode.Stateless,
        WorkingDirectory = workspacePath,
        ConfineWorkingDirectory = true,
        Timeout = TimeSpan.FromMinutes(10),
        MaxOutputBytes = 64 * 1024,
    }
);
```

Each fixed command becomes a parameterless `AIFunction`. Its body calls:

```csharp
await shell.RunAsync(authoredCommand, cancellationToken);
```

The formatted MAF result returns directly to the model during the current loop.

The model receives:

```text
run_tests()
```

It never receives:

```text
run_shell("task test")
```

This provides in-turn feedback without a pipeline capability transition or a
second process implementation.

## Initial Shell Lifecycle

Use `ShellMode.Stateless` for version one.

`AgentBlock` reconstructs live MAF agents during initial execution,
continuations, corrections, and checkpoint transitions. Tandem persists MAF
session JSON but does not currently own arbitrary run-scoped disposable
resources. MAF persistent shells require strict single-session ownership and
disposal.

Stateless mode is appropriate because:

- fixed commands do not need persistent `cd` or exported variables;
- each command starts in the configured workspace;
- no shell process survives between calls;
- executor lifetime cannot leak between agent reconstructions.

Persistent mode should wait for a general session-resource lifecycle mechanism.

## Unrestricted Shell

`"shell"` exposes MAF's standard model-authored command tool.

Tandem currently has no tool-approval interaction surface. An enabled local
shell therefore uses:

```csharp
AcknowledgeUnsafe = true
shell.AsAIFunction(requireApproval: false)
```

This must be documented without euphemism:

> Selecting `"shell"` grants the model unapproved local process execution with
> the Tandem host process's authority. The command starts in the configured
> workspace, but this is not filesystem or network isolation.

The tool-group predicate is application authorization, not process isolation.

MAF explicitly documents `ShellPolicy` allow/deny patterns as a bypassable UX
prefilter, not a security boundary. Tandem must not claim otherwise.

Future work may add:

- human tool approval;
- `DockerShellExecutor`;
- persistent shell sessions.

Those are not prerequisites for fixed repository commands.

## Expanded `git:ro`

Retain direct Git invocation with `UseShellExecute = false` and `ArgumentList`.
The model selects typed operation parameters, not shell text. This avoids shell
interpolation and enables operation-specific validation and pagination.

### `git_status`

Run:

```sh
git status --porcelain=v1 --branch --untracked-files=all
```

### `git_diff`

Inspect current workspace changes with:

- optional relative path;
- `staged` selector;
- pagination.

Commands:

```sh
git diff --no-ext-diff --no-textconv --no-color
git diff --cached --no-ext-diff --no-textconv --no-color
```

### `git_log`

Support:

- optional revision/range;
- optional relative path;
- bounded skip/count;
- fixed machine-readable formatting;
- no color, decoration, pager, or interactive input.

### `git_show`

Support:

- validated full revision;
- optional relative path;
- pagination;
- no external diff or text conversion.

### `git_blame`

Support:

- required repository-relative path;
- optional validated revision;
- optional bounded line range;
- bounded output.

### `git_changed_files`

Retain current paginated name/status comparison between exact revisions.

### `git_compare`

Rename the current exact base/candidate textual `git_diff` behavior to avoid
confusion with workspace `git_diff`.

Support:

- exact base SHA;
- exact candidate SHA;
- optional path;
- pagination.

### Shared Git requirements

- resolve `git` through `PATH`;
- fixed working directory;
- `UseShellExecute = false`;
- `ArgumentList`, never shell interpolation;
- full revision validation where required;
- repository-relative path validation;
- `.git` rejection;
- timeout and cancellation;
- process-tree termination;
- bounded output;
- `--no-ext-diff`;
- `--no-textconv`;
- no pager or interactive prompts.

Remove `.WithReadOnlyGit()`. The explicit `"git:ro"` bundle becomes the only
configuration path.

## Internal Model

Replace:

```csharp
Func<TState, string>? WorkspacePath
Func<TState, bool>? AllowMutation
bool ExposeReadOnlyGitTools
```

with:

```csharp
AgentWorkspaceDescriptor<TState>? Workspace
```

Containing:

```csharp
Func<TState, string> Path
Func<TState, IReadOnlyList<AgentCommandDescriptor>> Commands
IReadOnlyList<AgentToolGroupDescriptor<TState>> ToolGroups
```

At every `AgentBlock.CreateAgent` call:

1. Resolve workspace path.
2. Resolve repository command catalogue.
3. Evaluate every tool-group predicate against current state.
4. Expand built-ins and bundles.
5. Resolve the repository-command marker.
6. Reject duplicate and colliding effective names.
7. Build selected MAF file providers and shell-backed command tools.
8. Register every tool effect.
9. Construct the harness agent.

`AgentImplementationContext` should receive a resolved immutable plan, for
example:

```csharp
internal sealed record ResolvedAgentWorkspace(
    string Path,
    IReadOnlySet<WorkspaceToolKind> FileTools,
    bool IncludeGitReadOnly,
    bool IncludeShell,
    IReadOnlyList<AgentCommandDescriptor> Commands
);
```

Do not add further booleans directly to `AgentBlockConfig`.

## Observation And Guards

All tools must participate in the existing `ToolOutcomeCollector` and
`ToolEffectRegistry`:

- Git tools: `Read` plus `RepositoryInspection`;
- fixed commands: `ProcessExecution`;
- shell: `ProcessExecution`;
- file tools: existing read or workspace-mutation effects;
- capabilities: lifecycle transition.

Existing behavior remains:

- tool interception runs before execution;
- successful tool names enter `AgentTurnObservation.ToolNames`;
- state guards can block classified effects;
- latched gates can block effects during the same visit;
- unclassified tools fail gated-agent construction.

No additional command-observation protocol is needed for model-loop tools.
Pipeline-owned authoritative verification retains its existing operation-level
command observation.

## TypeScript Public Contracts

```ts
export type AgentToolName =
  | "read_file"
  | "ls"
  | "grep"
  | "write_file"
  | "delete_file"
  | "replace"
  | "replace_lines"
  | "git:ro"
  | "shell";

export interface AgentCommand {
  readonly name: string;
  readonly description: string;
  readonly command: string;
}
```

`agentWorkspace(...)` supports static or dynamic commands:

```ts
agentWorkspace({ path, commands: [...] });
agentWorkspace({ path, commands: (state) => [...] });
```

`repository.commands` is an opaque selection marker, not the command array.

`agentTools.always(...)` and `agentTools.when(...)` accept built-in names or that
marker.

## TypeScript Bridge Version 8

The current registration contract is version 7. This feature changes the agent
registration shape and increments it to version 8.

Add to `RegisteredNodeContract`:

```csharp
RegisteredWorkspaceContract? Workspace
```

With:

```csharp
internal sealed record RegisteredWorkspaceContract(
    string? PathCallback,
    string? CommandsCallback,
    RegisteredToolGroupContract[]? ToolGroups
);

internal sealed record RegisteredToolGroupContract(
    string[]? Tools,
    bool IncludeCommands,
    string? WhenCallback
);
```

The TypeScript SDK registers:

- one path callback;
- one command-catalogue callback, including for static catalogues;
- one predicate callback per conditional group.

Command callback result:

```json
[
  {
    "name": "run_tests",
    "description": "Run the complete test suite.",
    "command": "task test"
  }
]
```

The bridge must add a `Tandem.Advanced` project/package reference and construct
the same `AgentWorkspace<JavaScriptState>` and tool groups used by C# consumers.

Nested parallel agents retain complete workspace declarations. Workspace fields
are forbidden on non-agent nodes.

## Validation

Fail before a model call for:

- missing or blank workspace path;
- empty tool groups;
- unknown built-in names;
- duplicate selections within a group;
- duplicate effective tools across groups;
- command marker selected without a command catalogue;
- duplicate command names;
- invalid command function names;
- blank descriptions or commands;
- collisions with capabilities;
- collisions with skill tools;
- collisions with file, Git, shell, or command tools;
- workspace-bound tools without a workspace;
- malformed bridge callbacks;
- invalid callback output.

Dynamic command catalogues are validated every time the agent is constructed for
a visit.

## C# Tests

### Authoring

- static workspace commands;
- state-derived workspace commands;
- one workspace reused by multiple agents;
- role-specific access groups;
- unknown and duplicate selections rejected;
- command/capability and command/built-in collisions rejected;
- old `WithWorkspace` overload removed;
- `.WithReadOnlyGit()` removed from public API.

### File tools

Using real MAF behavior:

- selected read tool appears;
- unselected read tool is absent;
- selected mutation tool appears only when its predicate is true;
- unselected mutation tools remain absent;
- `.git` remains inaccessible;
- filtering preserves MAF instructions and execution.

### Fixed commands

Using a temporary repository and real `LocalShellExecutor`:

- parameterless command tool appears;
- authored command runs in workspace;
- stdout, stderr, exit code, and timeout return to the same model turn;
- model cannot supply replacement command text;
- timeout and cancellation terminate execution;
- output truncation is honored;
- command is classified `ProcessExecution`;
- state guard blocks it before execution.

### Shell

- absent unless selected;
- false predicate removes it from schema;
- true predicate exposes MAF `run_shell`;
- execution comes from MAF, not Tandem process code;
- starts in workspace;
- classified `ProcessExecution`;
- guard denial prevents execution;
- cancellation works;
- stateless calls do not retain `cd` or environment changes.

### Git

Use a real temporary Git repository for:

- status;
- staged and unstaged diff;
- log;
- show;
- blame;
- changed-file comparison;
- exact textual comparison;
- pagination;
- invalid revisions;
- traversal and `.git` rejection;
- external diff and text-conversion suppression;
- timeout and cancellation characterization.

## TypeScript Tests

- positive fixture with reusable workspace and role-specific access;
- negative fixtures for unknown tools and malformed commands;
- registration contract version 8 validation;
- callback-reference validation;
- nested parallel agent workspace support;
- dynamic commands derived from JavaScript state;
- fixed command through the packed runtime;
- tool disappears when a predicate becomes false;
- `"git:ro"` exposes the complete bundle;
- `"shell"` executes through packaged MAF shell dependency;
- packed consumer includes required MAF shell assemblies.

## Tandem Delivery Migration

Replace current uses of:

```csharp
.UseHarness(...)
.WithWorkspace(path, allowMutation)
.WithReadOnlyGit()
```

with one shared Delivery workspace environment.

Suggested role access:

- Executor: file reads, packet verification commands, conditionally exposed file
  mutations, and `git:ro` where useful.
- Planner: only required read tools.
- Reviewer: file reads, `git:ro`, and repository verification commands when
  independent reruns are useful.

The existing deterministic verification stage remains authoritative and
candidate-bound.

## Cadence Migration

Cadence is an external packaged Tandem consumer. Its packet already owns:

```csharp
IReadOnlyList<string> Verification
```

Create one `AgentWorkspace<CadenceState>` from:

```csharp
state.WorkspacePath
state.Packet.Verification
```

Attach it to Executor and Reviewer.

Agent command results are exploratory evidence only. They must not populate:

- `VerificationResults`;
- `VerifiedCandidateSha`.

Only `VerificationOperation` produces authoritative verification state after
candidate capture.

This gives the Executor the normal loop:

```text
inspect -> edit -> run test -> adjust -> submit
```

while retaining:

```text
capture candidate -> run complete authoritative verification -> review
```

## Dirty Worktree Constraint

Implementation must preserve and work directly on top of the existing dirty
read-only Git changes. Do not discard, reset, or recreate them from the plan.

Before editing each existing symbol, run the required GitNexus upstream impact
analysis. Before committing, run `detect_changes` against the staged result.

## Implementation Sequence

1. Characterize `Microsoft.Agents.AI.Tools.Shell`
   `1.16.0-preview.260730.1` against Tandem's net10/MAF 1.16 package set.
2. Add the official shell package to `Tandem.Advanced`.
3. Add `ProcessExecution` to public/internal tool effects and mappings.
4. Add `AgentCommand`, `AgentWorkspace<TState>`, tool selections, and groups.
5. Replace workspace booleans in `AgentBuilder`, `AgentBlockConfig`, and
   `AgentImplementationContext` with descriptors/resolved plans.
6. Resolve workspace commands and tool predicates per agent visit.
7. Add the filtering adapter around MAF file tools.
8. Convert the dirty Git implementation to `"git:ro"` and expand the bundle.
9. Add MAF-backed fixed command tools.
10. Add stateless unrestricted MAF shell.
11. Register all effects and integrate guards/observations.
12. Migrate Tandem Delivery.
13. Remove `.WithReadOnlyGit()` and the old `WithWorkspace` overload.
14. Add TypeScript workspace/tool authoring.
15. Increment the bridge contract from 7 to 8.
16. Translate bridge workspaces through the C# Advanced API.
17. Add C#, bridge, Node, type, and packed-runtime tests.
18. Update README, TypeScript README, public API manifests, and stabilization
    plans.
19. Pack the updated Tandem packages into Cadence.
20. Migrate Cadence Executor and Reviewer to the shared repository workspace.
21. Run all Tandem and Cadence tests, package checks, formatting, and
    `~/Sites/plumb/plumb . --json`.

## Acceptance Criteria

The slice is complete when:

- C# and TypeScript express the same workspace/tool policy;
- repository commands are declared once and reused across agents;
- Executor can call `run_tests()` and receive output in the same model loop;
- the model cannot alter fixed command text;
- authoritative verification remains a separate candidate-bound stage;
- individual MAF file tools can be selected;
- conditional groups visibly control tool exposure;
- `"git:ro"` supplies the complete bounded Git inspection bundle;
- `.WithReadOnlyGit()` no longer exists;
- fixed commands use MAF shell mechanics without Tandem process code;
- `"shell"` uses MAF's implementation without Tandem process code;
- process execution is independently classified and gateable;
- dynamic Cadence packet commands work;
- nested TypeScript parallel agents retain workspace declarations;
- bridge contract version 8 rejects malformed policies;
- package consumers contain and execute the MAF shell dependency;
- all Tandem and Cadence tests and mechanical architecture gates pass.
