# Limitations

These are the deliberate limits of Tandem, so you don't discover them in production.

## Runs

- **Runs are process-owned.** If the process stops, the run stops. The ledger can't reopen or
  resume it.
- **Interactions wait in memory.** A long human wait means keeping the process alive, or ending
  the run and starting another when the answer arrives.
- **No exactly-once side effects.** Side effects in stages, commands and parallel branches
  aren't rolled back when something later fails.

## Platforms

- C# needs .NET 10.
- The TypeScript SDK runs on macOS (Apple silicon) and Linux x64 only, needs Node.js 22 or
  newer, and needs the .NET 10 runtime. It's ESM-only.
- Ledger files from before Tandem 0.3.0 (schema version 1) are refused, with no migration.

## Graph shape

- Parallel branches can be agents or stages only. No outputs, interactions, nested parallel
  groups or subgraphs as branches.
- A branch participant can't also appear elsewhere in the parent graph.
- A collection's item operation can only call the agents declared on it, one at a time.
- Outcomes are only `Success` and `Failed`. Domain decisions go in state.

## Agents and tools

- Structured output gets exactly one corrective response before the agent fails.
- The workspace is the starting directory for processes, not a sandbox. Commands and `shell` run
  with the host process's full filesystem and network authority.
- Skills are instructions only. Their scripts can't run.
- Model request settings are requests. The endpoint decides which it supports.
- `web_search` and `web_fetch` use Tavily, and need `TAVILY_API_KEY`.
