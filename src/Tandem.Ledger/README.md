# Meridian.Tandem.Ledger

An append-only SQLite run-history journal for Tandem pipelines: accepted values and run records
for inspection. Runs themselves are process-owned; a ledger cannot resume them.

```sh
dotnet add package Meridian.Tandem.Ledger --version 0.1.0
```

```csharp
using Tandem;
using Tandem.Ledger;

var result = await new PipelineRunner().RunAsync(
    pipeline,
    initialState,
    new SqlitePipelineRunOptions("runs.sqlite3"),
    cancellationToken);
```

Mark the pipeline or selected participants for persistence. The SQLite runner owns observer setup
and run terminalization; application state remains free of runtime bookkeeping.

A custom host that creates the observer itself (`store.CreateObserverAsync(runId, pipeline)`) passes it
through `PipelineObservers.Compose(...)` and wraps the run in `store.RecordRunAsync(runId, ...)`, which
completes the ledger run as `Ready`, `Failed`, `Cancelled` or `Faulted`.
