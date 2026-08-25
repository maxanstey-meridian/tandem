# Meridian.Tandem.Ledger

SQLite-backed accepted-value persistence and run records for Tandem pipelines.

```sh
dotnet add package Meridian.Tandem.Ledger --version 0.1.0-alpha.1
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
