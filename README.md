# Tandem

Tandem runs durable, configurable pipelines for agentic software work. It gives
planner, executor, verification, reviewer, and human-input operations one shared
run context, then uses workflow composition to decide what happens next.

The included `simple-v1` pipeline can:

- prepare an isolated Git workspace pinned to the requested base commit;
- ask a planner for guidance before mutation;
- run a sessioned implementation agent with validated lifecycle tools;
- execute packet-defined verification commands;
- review the exact candidate that was verified;
- suspend for human input and resume later;
- stream the run through a terminal dashboard; and
- publish a Ready candidate as a local branch without changing your checkout.

## How It Works

Tandem composes reusable blocks into a Microsoft Agent Framework workflow. A
block performs one operation and emits an outcome. Ordered route conditions
inspect that outcome and the durable pipeline context to select the next block.

Each composition owns one concrete immutable state record. Tandem carries it in
`PipelineMessage<TState>` beside a small runtime envelope containing only run ID,
agent sessions, usage, and invocation counts. Reusable agent blocks receive
composition-supplied message, workspace, structured-output, checkpoint, and MCP
receipt transitions; they do not infer planner, executor, or reviewer roles.

```text
executor asks for guidance      -> planner
planner approves                -> executor
planner needs a decision        -> human input
verification fails              -> executor
all verification passes         -> reviewer
reviewer requests changes       -> executor
reviewer accepts                -> complete
```

Microsoft Agent Framework owns workflow execution, durable continuation, agent
sessions, model loops, and tool dispatch. Tandem owns product blocks,
composition, prompts, policies, Git operations, validated boundaries, and the
operator interface.

The central invariant is:

```text
The configured pipeline is the lifecycle.
The runtime only executes it durably.
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the architectural boundaries.

## Requirements

- .NET SDK 10.0.300 or a compatible 10.0 feature band
- Docker for the Durable Task Scheduler emulator
- [Task](https://taskfile.dev/) for repository development commands
- An OpenAI-compatible model provider

## Configuration

Tandem reads `$TANDEM_HOME/config.json`. If `TANDEM_HOME` is unset, it uses the
platform local application-data directory under `Tandem`.

The `simple-v1` pipeline requires profiles named `implementation`, `planning`,
and `review`:

```json
{
    "providers": {
        "openrouter": {
            "type": "openai",
            "baseUrl": "https://openrouter.ai/api/v1",
            "apiKeyEnvironmentVariable": "OPENROUTER_API_KEY",
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
        },
        "planning": {
            "provider": "openrouter",
            "model": "anthropic/claude-sonnet-4.5",
            "reasoningEffort": "high",
            "contextWindowTokens": 200000,
            "maxOutputTokens": 32000,
            "checkpointAtPercent": 80
        },
        "review": {
            "provider": "openrouter",
            "model": "anthropic/claude-sonnet-4.5",
            "reasoningEffort": "high",
            "contextWindowTokens": 200000,
            "maxOutputTokens": 32000,
            "checkpointAtPercent": 80
        }
    }
}
```

Set the provider credential named by `apiKeyEnvironmentVariable`:

```sh
export OPENROUTER_API_KEY="..."
```

Both OpenAI-compatible chat completions (`completions`) and Responses API
(`responses`) transports are supported.

## Run Tandem

Start the Durable Task Scheduler emulator:

```sh
docker run -d --name tandem-dts \
  -p 8080:8080 -p 8082:8082 \
  -e DTS_TASK_HUB_NAMES=tandem-cli,tandem-tests \
  mcr.microsoft.com/dts/dts-emulator:latest
```

Run the included example packet:

```sh
dotnet run --project src/Tandem -- run examples/01-todo-api/packet.md
```

The dashboard displays the run ID. A disconnected run can be reopened with:

```sh
dotnet run --project src/Tandem -- attach <run-id>
```

After a run reaches `Ready`, publish its candidate as a local branch:

```sh
dotnet run --project src/Tandem -- publish <run-id> --branch tandem/my-change
```

Set `TANDEM_DTS_CONNECTION_STRING` to use a scheduler other than the local
emulator.

## Packets

A packet identifies a repository and base revision, declares observable
outcomes, supplies verification commands and constraints, and may add free-form
implementation context. See [`examples/01-todo-api/packet.md`](examples/01-todo-api/packet.md)
for a complete example.

## Development

```sh
dotnet tool restore
task check
```

Useful individual commands are `task build`, `task test`, `task format`, and
`task format:check`.
