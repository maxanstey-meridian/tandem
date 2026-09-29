using Tandem;
using Tandem.Ledger;

if (args.Length != 3 || !Guid.TryParse(args[1], out var runId))
{
    return 2;
}

var store = new SqliteLedgerStore(args[0]);
var observer = await store.CreateObserverAsync(runId, "process-contention");
await observer.ObserveAsync(new PipelineStepStarted(runId, args[2]), CancellationToken.None);
return 0;
