namespace Tandem;

internal sealed record PipelineLedgerEntry(
    long Cursor,
    string Stream,
    long Sequence,
    string EntryId,
    string Value,
    DateTimeOffset RecordedAt
);

internal sealed record PipelineLedgerPage(
    IReadOnlyList<PipelineLedgerEntry> Entries,
    long? NextCursor
)
{
    public int ReturnedCount => Entries.Count;
    public bool HasMore => NextCursor is not null;
    public int MaximumPageSize => 50;
    public string Pagination =>
        "Use nextCursor with the same query until hasMore is false. The cursor identifies a ledger record, not an offset or page number. Pages may end early at the response-size limit; total matching records/pages are not computed. Use read_ledger_entry with an entry cursor to retrieve its complete value in pages.";
}

internal interface IPipelineLedgerReader
{
    public ValueTask<long?> FindActionEntryAsync(
        string stepId,
        string invocationId,
        CancellationToken cancellationToken = default
    );

    public ValueTask<object> ReadDiagnosticAsync(
        long entryCursor,
        string stream,
        int offset = 0,
        int limit = 16000,
        CancellationToken cancellationToken = default
    );

    public ValueTask<object> ReadEntryAsync(
        long entryCursor,
        int offset = 0,
        int limit = 16000,
        CancellationToken cancellationToken = default
    );

    public ValueTask<PipelineLedgerPage> ReadAsync(
        long? cursor = null,
        int limit = 20,
        CancellationToken cancellationToken = default
    );

    public ValueTask<PipelineLedgerPage> SearchAsync(
        string query,
        long? cursor = null,
        int limit = 20,
        CancellationToken cancellationToken = default
    );
}
