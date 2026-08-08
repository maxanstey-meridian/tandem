using Tandem.Ledger;

if (args.Length != 4 || !Guid.TryParse(args[1], out var runId))
{
    return 2;
}

var store = new SqliteLedgerStore(args[0]);
await store.InitializeAsync();
await store.CreateRunAsync(runId, "process-contention");
var accepted = await store
    .ForRun(runId)
    .AppendAsync(
        new LedgerStream<WorkerEntry>("process-entries", "test.process-entry"),
        args[2],
        new WorkerEntry(args[3])
    );
Console.WriteLine(accepted.Sequence);
return 0;

internal sealed record WorkerEntry(string Value);
