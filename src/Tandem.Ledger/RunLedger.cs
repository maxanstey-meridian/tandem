namespace Tandem.Ledger;

public sealed class RunLedger : IPipelineLedgerReader
{
    private readonly SqliteLedgerStore _store;

    internal RunLedger(SqliteLedgerStore store, Guid runId)
    {
        _store = store;
        RunId = runId;
    }

    public Guid RunId { get; }

    ValueTask<long?> IPipelineLedgerReader.FindActionEntryAsync(
        string stepId,
        string invocationId,
        CancellationToken cancellationToken
    ) => _store.FindActionEntryAsync(RunId, stepId, invocationId, cancellationToken);

    ValueTask<object> IPipelineLedgerReader.ReadDiagnosticAsync(
        long entryCursor,
        string stream,
        int offset,
        int limit,
        CancellationToken cancellationToken
    ) => _store.ReadEntryPageAsync(RunId, entryCursor, offset, limit, cancellationToken, stream);

    ValueTask<object> IPipelineLedgerReader.ReadEntryAsync(
        long entryCursor,
        int offset,
        int limit,
        CancellationToken cancellationToken
    ) => _store.ReadEntryPageAsync(RunId, entryCursor, offset, limit, cancellationToken);

    ValueTask<PipelineLedgerPage> IPipelineLedgerReader.ReadAsync(
        long? cursor,
        int limit,
        CancellationToken cancellationToken
    ) => _store.ReadPageAsync(RunId, null, cursor, limit, cancellationToken);

    ValueTask<PipelineLedgerPage> IPipelineLedgerReader.SearchAsync(
        string query,
        long? cursor,
        int limit,
        CancellationToken cancellationToken
    ) => _store.ReadPageAsync(RunId, query, cursor, limit, cancellationToken);
}
