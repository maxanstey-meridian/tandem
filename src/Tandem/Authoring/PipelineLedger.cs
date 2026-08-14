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
);

internal interface IPipelineLedgerReader
{
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
