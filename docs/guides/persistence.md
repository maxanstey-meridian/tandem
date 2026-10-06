# Persistence

Turn persistence on with `.Persist()` in C# or `persist: true` in TypeScript, either for the
whole pipeline or for selected participants. A persistent pipeline records:

- accepted structured agent outputs
- accepted capability calls
- interaction requests and answers
- declared failures
- state returned by persistent stages

Tandem records a value at the moment the stage, agent, capability or interaction accepts it.
C# can opt a single interaction out with `DoNotPersist(interaction)`.

## The ledger

The ledger is an append-only SQLite journal of run history, kept for inspection. Runs are owned
by their process: the ledger records what a run accepted, but it can't reopen or resume the
run.

::: code-group

```csharp [C#]
// dotnet add package Meridian.Tandem.Ledger
using Tandem.Ledger;

var result = await new PipelineRunner().RunAsync(
    pipeline,
    initialState,
    new SqlitePipelineRunOptions("runs.sqlite3"),
    cancellationToken);
```

```ts [TypeScript]
const result = await run(pipeline, initialState, { ledgerPath: "runs.sqlite3" });
```

:::

`SqlitePipelineRunOptions` creates the ledger run, supplies the persistence observer, and
records the run's terminal status. A custom host can do those steps itself: create the
observer with `store.CreateObserverAsync(runId, pipeline)`, combine it with its own observers
through `PipelineObservers.Compose(persistence, live)`, and wrap the run in
`store.RecordRunAsync(runId, () => runner.RunAsync(...))`. That completes the ledger run as
`Ready`, `Failed`, `Cancelled` or `Faulted`.

The ledger file uses schema version 2. Files written before Tandem 0.3.0 are refused, with no
migration. Start a new file.

## Inspecting accepted values

Your application owns the ledger path and any operator-facing view of it.

::: code-group

```csharp [C#]
var store = new SqliteLedgerStore("runs.sqlite3");
var accepted = await store.ReadAcceptedAsync(runId);
```

```ts [TypeScript]
import { inspectAccepted } from "@maxanstey-meridian/tandem";

const accepted = await inspectAccepted({ ledgerPath: "runs.sqlite3", runId });
```

:::

C# can also read the full journal with `ReadJournalAsync(runId)`, or just the latest accepted
value with `ReadLatestAcceptedAsync`. Reads open the database read-only. They never create a
missing file, and they refuse a file with another schema version.

## Ledger tools for agents

Persisting a run records history. It doesn't give agents access to it. The ledger tools
`read_ledger`, `search_ledger` and `read_ledger_entry` are **disabled by default**, so supply
every fact an agent needs in its message unless it really needs historical retrieval.

To grant them for a run, set `enableLedgerTools: true` alongside `ledgerPath` in TypeScript, or
`EnableLedgerTools = true` on C# `SqlitePipelineRunOptions`. That grants access to every agent
in the run. A C# host using plain `PipelineRunOptions` can attach a reader explicitly with
`options.WithRunLedger(ledger)`.

- `read_ledger` and `search_ledger` return bounded excerpts.
- Pass an entry's cursor to `read_ledger_entry` for the complete value, following
  `nextOffset` until `hasMore` is false.
- Retrieval is limited to the current run and to records agents are allowed to read.
- `search_ledger` matches JSON values only, not property names.
