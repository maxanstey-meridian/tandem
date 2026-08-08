namespace Tandem.Ledger;

public sealed class RunLedger
{
    private readonly SqliteLedgerStore _store;

    internal RunLedger(SqliteLedgerStore store, Guid runId)
    {
        _store = store;
        RunId = runId;
    }

    public Guid RunId { get; }

    public ValueTask<AcceptedLedgerEntry<TEntry>> AppendAsync<TEntry>(
        LedgerStream<TEntry> stream,
        string entryId,
        TEntry entry,
        CancellationToken cancellationToken = default
    ) => _store.AppendAsync(RunId, stream, entryId, entry, cancellationToken);

    public ValueTask<IReadOnlyList<AcceptedLedgerEntry<TEntry>>> ReadAsync<TEntry>(
        LedgerStream<TEntry> stream,
        CancellationToken cancellationToken = default
    ) => _store.ReadAsync(RunId, stream, cancellationToken);

    public ValueTask<IReadOnlyList<AcceptedLedgerEntry<TEntry>>> ReadRecentAsync<TEntry>(
        LedgerStream<TEntry> stream,
        int limit,
        CancellationToken cancellationToken = default
    ) => _store.ReadRecentAsync(RunId, stream, limit, cancellationToken);

    public ValueTask<LedgerDocumentValue<TDocument>?> ReadDocumentAsync<TDocument>(
        LedgerDocument<TDocument> document,
        CancellationToken cancellationToken = default
    ) => _store.ReadDocumentAsync(RunId, document, cancellationToken);

    public ValueTask<LedgerDocumentValue<TDocument>> WriteDocumentAsync<TDocument>(
        LedgerDocument<TDocument> document,
        TDocument value,
        long expectedVersion,
        CancellationToken cancellationToken = default
    ) => _store.WriteDocumentAsync(RunId, document, value, expectedVersion, cancellationToken);
}
