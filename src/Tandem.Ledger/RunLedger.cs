using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Infrastructure;

namespace Tandem.Ledger;

/// <summary>
/// One run's ledger as agents see it through the ledger tools: bounded pages of the run's
/// agent-readable journal records.
/// </summary>
public sealed class RunLedger : IPipelineLedgerReader
{
    private const string JournalStream = "runtime.journal";
    private const int MaximumPageSize = 50;
    private const int MaximumQueryLength = 1_024;
    private const int MaximumValueCharacters = 4_000;
    private const int MaximumPageCharacters = 200_000;
    private const int MinimumEntryPageLength = 2;
    private const int MaximumEntryPageLength = 65_536;
    private const string Truncated = "[...truncated...]";

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

    ValueTask<PipelineLedgerPage> IPipelineLedgerReader.ReadAsync(
        long? cursor,
        int limit,
        CancellationToken cancellationToken
    ) => ReadPageAsync(null, cursor, limit, cancellationToken);

    ValueTask<PipelineLedgerPage> IPipelineLedgerReader.SearchAsync(
        string query,
        long? cursor,
        int limit,
        CancellationToken cancellationToken
    ) => ReadPageAsync(query, cursor, limit, cancellationToken);

    async ValueTask<object> IPipelineLedgerReader.ReadEntryAsync(
        long entryCursor,
        int offset,
        int limit,
        CancellationToken cancellationToken
    )
    {
        ValidateEntryPage(entryCursor, offset, limit);
        var record = await ReadAgentReadableRecordAsync(entryCursor, cancellationToken);
        return Slice(entryCursor, null, null, record, offset, limit);
    }

    async ValueTask<object> IPipelineLedgerReader.ReadDiagnosticAsync(
        long entryCursor,
        string stream,
        int offset,
        int limit,
        CancellationToken cancellationToken
    )
    {
        ValidateEntryPage(entryCursor, offset, limit);
        var record = await ReadAgentReadableRecordAsync(entryCursor, cancellationToken);
        if (stream is not ("stdout" or "stderr"))
        {
            throw new ToolInputException("stream must be stdout or stderr.");
        }
        if (
            JsonSerializer.Deserialize<RuntimeJournalRecord>(record, SqliteLedgerStore.JournalJson)
            is not { Kind: RuntimeJournalKind.ActionCompleted, Payload: { } processPayload }
        )
        {
            throw new ToolInputException(
                "This entry has no process diagnostics. Read it without stream."
            );
        }
        var process =
            processPayload.Deserialize<PipelineActionProcessPayload>()
            ?? throw new LedgerDataException("Missing process output.");
        return Slice(
            entryCursor,
            stream,
            process.Truncated,
            stream == "stdout" ? process.Stdout : process.Stderr,
            offset,
            limit
        );
    }

    private async ValueTask<PipelineLedgerPage> ReadPageAsync(
        string? query,
        long? cursor,
        int limit,
        CancellationToken cancellationToken
    )
    {
        ValidatePage(query, cursor, limit);
        var rows = await _store.ReadAgentReadableAsync(
            RunId,
            cursor ?? 0,
            query,
            limit + 1,
            cancellationToken
        );
        var entries = new List<PipelineLedgerEntry>();
        var pageCharacters = 0;
        foreach (var row in rows)
        {
            var value = Excerpt(row.Record, query);
            if (entries.Count == limit || pageCharacters + value.Length > MaximumPageCharacters)
            {
                return new PipelineLedgerPage(entries, entries[^1].Cursor);
            }
            entries.Add(
                new PipelineLedgerEntry(
                    row.Id,
                    JournalStream,
                    row.Sequence,
                    $"{JournalStream}-{row.Sequence}",
                    value,
                    row.RecordedAt
                )
            );
            pageCharacters += value.Length;
        }
        return new PipelineLedgerPage(entries, null);
    }

    private static void ValidatePage(string? query, long? cursor, int limit)
    {
        if (cursor < 0)
        {
            throw new PaginationValidationException(
                nameof(cursor),
                "Cursor cannot be negative. Restart with a null cursor; subsequently use nextCursor from the preceding page.",
                new { cursor, retryCursor = (long?)null }
            );
        }
        if (limit is < 1 or > MaximumPageSize)
        {
            throw new PaginationValidationException(
                nameof(limit),
                "Ledger page size must be 1 to 50.",
                new
                {
                    limit,
                    minimumLimit = 1,
                    maximumLimit = MaximumPageSize,
                    retryLimit = Math.Clamp(limit, 1, MaximumPageSize),
                }
            );
        }
        if (query is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new PaginationValidationException(
                nameof(query),
                "Supply nonblank search text, or use read_ledger to browse without a query.",
                new { minimumQueryLength = 1, maximumQueryLength = MaximumQueryLength }
            );
        }
        if (query.Length > MaximumQueryLength)
        {
            throw new PaginationValidationException(
                nameof(query),
                "Ledger search query cannot exceed 1024 characters. Shorten the query and restart with a null cursor.",
                new
                {
                    queryLength = query.Length,
                    maximumQueryLength = MaximumQueryLength,
                    retryCursor = (long?)null,
                }
            );
        }
    }

    private static void ValidateEntryPage(long entryCursor, int offset, int limit)
    {
        if (
            entryCursor <= 0
            || offset < 0
            || limit is < MinimumEntryPageLength or > MaximumEntryPageLength
        )
        {
            throw new PaginationValidationException(
                "page",
                "entryCursor must be positive, offset nonnegative, and limit from 2 to 65536.",
                new
                {
                    entryCursor,
                    retryOffset = 0,
                    retryLimit = Math.Clamp(limit, MinimumEntryPageLength, MaximumEntryPageLength),
                }
            );
        }
    }

    private async ValueTask<string> ReadAgentReadableRecordAsync(
        long entryCursor,
        CancellationToken cancellationToken
    )
    {
        var row =
            await _store.ReadRecordAsync(RunId, entryCursor, cancellationToken)
            ?? throw new PaginationValidationException(
                nameof(entryCursor),
                "No readable entry at this cursor in the current run. Use read_ledger or search_ledger to obtain an entry cursor.",
                new { entryCursor }
            );
        return row.IsAgentReadable
            ? row.Record
            : throw new PaginationValidationException(
                nameof(entryCursor),
                "This record is not agent-readable. Use a cursor from read_ledger or search_ledger.",
                new { entryCursor }
            );
    }

    private static LedgerEntryPage Slice(
        long entryCursor,
        string? stream,
        bool? captureTruncated,
        string value,
        int offset,
        int limit
    )
    {
        if (
            offset > value.Length
            || (
                offset > 0
                && offset < value.Length
                && char.IsLowSurrogate(value[offset])
                && char.IsHighSurrogate(value[offset - 1])
            )
        )
        {
            throw new PaginationValidationException(
                nameof(offset),
                "Offset exceeds the entry or splits a Unicode character. Restart at offset 0 and follow nextOffset.",
                new { totalLength = value.Length, retryOffset = 0 }
            );
        }

        var length = Math.Min(limit, value.Length - offset);
        if (
            length > 0
            && offset + length < value.Length
            && char.IsHighSurrogate(value[offset + length - 1])
        )
        {
            length--;
        }
        var next = offset + length;
        return new LedgerEntryPage(
            entryCursor,
            stream,
            captureTruncated,
            value.Substring(offset, length),
            offset,
            length,
            value.Length,
            next < value.Length,
            next < value.Length ? next : null
        );
    }

    private static string Excerpt(string value, string? query)
    {
        if (value.Length <= MaximumValueCharacters)
        {
            return value;
        }

        var start = 0;
        if (query is not null)
        {
            var match = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            start = Math.Max(0, match - MaximumValueCharacters / 4);
            start = Math.Min(start, value.Length - MaximumValueCharacters);
        }
        var prefix = start > 0 ? Truncated : "";
        var suffix = start + MaximumValueCharacters < value.Length ? Truncated : "";
        return $"{prefix}{value.Substring(start, MaximumValueCharacters)}{suffix}";
    }
}

/// <summary>
/// One page of a ledger entry's text, as returned by <c>read_ledger_entry</c>. The names are the
/// model-facing wire contract, so they don't depend on the caller's serializer options.
/// </summary>
internal sealed record LedgerEntryPage(
    [property: JsonPropertyName("entryCursor")] long EntryCursor,
    [property: JsonPropertyName("stream")] string? Stream,
    [property: JsonPropertyName("captureTruncated")] bool? CaptureTruncated,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("totalLength")] int TotalLength,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("nextOffset")] int? NextOffset
)
{
    [JsonPropertyName("offsetUnit")]
    public string OffsetUnit => "UTF-16 code units";
}
