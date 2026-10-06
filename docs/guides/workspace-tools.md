# Workspace Tools

Some agents need to work in a repository: read files, run the tests, make changes. Workspace
tools give an agent a directory and an explicit set of tools, chosen by your application rather
than the model.

In C#, workspace tools are part of `Meridian.Tandem.Advanced`, because they deliberately take
part in execution mechanics.

::: code-group

```csharp [C#]
using Tandem.Advanced;

var repository = AgentWorkspace<State>.Define(
    state => state.WorkspacePath,
    [
        // The model chooses this tool name; the application owns the command text.
        AgentCommand.Define("run_tests", "Run the complete test suite.", "task test"),
    ]);

var worker = Agent
    .Create<State>("worker", "Implement and verify the requested change.", clients.Worker)
    .UseHarness(harnessInstructions)
    .WithWorkspace(
        repository,
        [
            // Reads and application-declared commands are always available.
            AgentTools.Always<State>("read_file", "ls", "grep", "git:ro", repository.Commands),
            // File mutation follows application state, not model preference.
            AgentTools.When<State>(
                state => state.MutationAuthorized,
                "write_file", "delete_file", "replace", "replace_lines"),
        ])
    .WithMessage(state => state.Request)
    .Build();
```

```ts [TypeScript]
const repository = agentWorkspace<State>({
  path: (state) => state.workspacePath,
  commands: [
    // The model chooses this tool name; the application owns the command text.
    { name: "run_tests", description: "Run the complete test suite.", command: "task test" },
  ],
});

const worker = agent<State>({
  id: "worker",
  instructions: "Implement and verify the requested change.",
  client: clients.worker,
  message: (state) => state.request,
  workspace: repository.withTools([
    // Reads and application-declared commands are always available.
    agentTools.always("read_file", "ls", "grep", "git:ro", repository.commands),
    // File mutation follows application state, not model preference.
    agentTools.when((state: State) => state.mutationAuthorized,
      "write_file", "delete_file", "replace", "replace_lines"),
  ]),
});
```

:::

- Each agent receives only the tool groups passed to its own workspace configuration.
- An "always" group is exposed on every visit. A "when" group re-evaluates its predicate from
  current state before each visit.
- `git:ro` expands to bounded status, diff, log, show, blame, changed-file and exact-comparison
  tools.

## The tools

| Tool | Effect |
|---|---|
| `read_file`, `ls`, `grep` | Read |
| `git:ro` | Read-only Git |
| `write_file`, `delete_file`, `replace`, `replace_lines` | Workspace mutation |
| Workspace commands | Process execution, with the command text fixed by you |
| `shell` | Process execution, with the command text written by the model |
| `web_search`, `web_fetch` | Read, through [Tavily](https://tavily.com) |

The web tools need `TAVILY_API_KEY` in the environment. An agent that selects one without it
fails with an error naming the variable.

Reads use source line numbers, and long results page with `offset` and `nextOffset`. Grep is a
case-insensitive regex by default. Inside a Git work tree it searches files Git tracks, or
untracked files that aren't ignored, so `.gitignore` applies.

## Commands

`repository.Commands` (or `repository.commands`) selects the whole fixed command catalogue. By
default each command is a parameterless tool: the model can choose `run_tests`, but it can't
alter `task test` or append arguments.

To let the model pass arguments, define the command with an example `arguments` list. The model
may then supply up to 16 strings of up to 200 characters each. Each value is shell-quoted, but
program flags can still change program behaviour, so grant this only where you mean to.

Commands run without approval, with the host process's own filesystem and network authority.
Their output is feedback and evidence for the agent visit. Your application still decides which
deterministic stage or route makes verification authoritative.

A command's successful calls can be required as evidence before an agent's output is accepted.
A later failed call of the same command invalidates its earlier success.

::: warning The workspace is not a sandbox
The workspace path is only the starting directory for process execution. It isn't filesystem
or network isolation. Selecting `shell` lets the model write any command. Leave it out when an
agent should be limited to the commands you declared.
:::

File and Git tools do enforce a path boundary: paths outside the workspace, through `.git`, or
through symbolic links are rejected.
