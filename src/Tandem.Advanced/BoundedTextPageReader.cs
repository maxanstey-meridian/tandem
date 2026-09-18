using System.Buffers;
using System.Text;
using System.Text.Json.Serialization;
using Tandem.Infrastructure;

namespace Tandem.Advanced;

internal sealed record TextPage(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("totalLength")] int TotalLength,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property:
        JsonPropertyName("nextOffset"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)
    ]
        int? NextOffset
)
{
    [JsonPropertyName("offsetUnit")]
    public string OffsetUnit => "UTF-16 code units";

    [JsonPropertyName("maximumLimit")]
    public int MaximumLimit => BoundedTextPageReader.MaximumLimit;

    [JsonPropertyName("pagination")]
    public string Pagination =>
        "Use nextOffset with the same file/search arguments until hasMore is false. Results are recomputed; restart at offset 0 if the input or query changes. totalLength is the current result size, not a snapshot guarantee.";
}

internal static class BoundedTextPageReader
{
    internal const int DefaultLimit = 64 * 1024;
    internal const int MaximumLimit = 64 * 1024;

    internal static async Task<TextPage> ReadAsync(
        IAsyncEnumerable<string> chunks,
        int offset = 0,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default
    )
    {
        ValidateBounds(offset, limit);
        var accumulator = new StreamedPageAccumulator(offset, limit);
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            accumulator.Append(chunk);
        }
        return accumulator.Complete();
    }

    internal static async Task<TextPage> ReadAsync(
        string path,
        int offset = 0,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default
    )
    {
        ValidateBounds(offset, limit);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false
        );
        var buffer = ArrayPool<char>.Shared.Rent(4096);
        var page = new StringBuilder(Math.Min(limit, 4096));
        var position = 0;
        char? previous = null;
        char? characterAtOffset = null;
        char? characterBeforeOffset = null;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(0, 4096), cancellationToken)) > 0)
            {
                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\0')
                    {
                        throw new InvalidDataException(
                            "The requested file is not safe textual content."
                        );
                    }
                    if (position == offset)
                    {
                        characterAtOffset = character;
                        characterBeforeOffset = previous;
                    }
                    if (position >= offset && page.Length < limit)
                    {
                        page.Append(character);
                    }
                    previous = character;
                    position = checked(position + 1);
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        if (offset > position)
        {
            throw InvalidPage(
                nameof(offset),
                $"Offset {offset} exceeds the current result length {position}. Results may have changed, or the offset may belong to another query. Restart at offset 0; then follow nextOffset with unchanged arguments until hasMore is false.",
                offset,
                limit,
                position,
                0,
                limit
            );
        }
        if (
            offset > 0
            && characterAtOffset is { } first
            && char.IsLowSurrogate(first)
            && characterBeforeOffset is { } before
            && char.IsHighSurrogate(before)
        )
        {
            throw InvalidPage(
                nameof(offset),
                "Offset splits a Unicode surrogate pair. Retry at the preceding complete character boundary.",
                offset,
                limit,
                position,
                offset - 1,
                Math.Max(limit, 2)
            );
        }

        if (page.Length > 0 && char.IsHighSurrogate(page[^1]) && offset + page.Length < position)
        {
            page.Length--;
            if (page.Length == 0)
            {
                throw InvalidPage(
                    nameof(limit),
                    "Limit is too small for the Unicode character at this offset. Retry with a limit of at least 2 UTF-16 code units.",
                    offset,
                    limit,
                    position,
                    offset,
                    2
                );
            }
        }
        var content = page.ToString();
        var nextOffset = offset + content.Length;
        var hasMore = nextOffset < position;
        return new TextPage(
            content,
            offset,
            content.Length,
            position,
            hasMore,
            hasMore ? nextOffset : null
        );
    }

    internal static void ValidateBounds(int offset, int limit)
    {
        if (offset < 0)
        {
            throw InvalidPage(
                nameof(offset),
                "Offset cannot be negative. Retry at offset 0.",
                offset,
                limit,
                null,
                0,
                Math.Clamp(limit, 1, MaximumLimit)
            );
        }
        if (limit is < 1 or > MaximumLimit)
        {
            throw InvalidPage(
                nameof(limit),
                $"Limit must be from 1 to {MaximumLimit} UTF-16 code units.",
                offset,
                limit,
                null,
                Math.Max(offset, 0),
                Math.Clamp(limit, 1, MaximumLimit)
            );
        }
    }

    private static PaginationValidationException InvalidPage(
        string parameter,
        string message,
        int offset,
        int limit,
        int? totalLength,
        int retryOffset,
        int retryLimit
    ) =>
        new(
            parameter,
            message,
            new
            {
                offset,
                limit,
                totalLength,
                maximumOffset = totalLength,
                minimumLimit = 1,
                maximumLimit = MaximumLimit,
                offsetUnit = "UTF-16 code units",
                retryOffset,
                retryLimit,
            }
        );

    private sealed class StreamedPageAccumulator(int offset, int limit)
    {
        private readonly StringBuilder _page = new(Math.Min(limit, 4096));
        private int _total;
        private char? _characterBeforeOffset;
        private char? _characterAtOffset;
        private char? _previousCharacter;

        internal void Append(string value)
        {
            var start = _total;
            _total = checked(_total + value.Length);
            if (start <= offset && offset < _total)
            {
                _characterAtOffset = value[offset - start];
                _characterBeforeOffset =
                    offset > start ? value[offset - start - 1] : _previousCharacter;
            }
            var from = Math.Max(offset, start);
            var to = (int)Math.Min(_total, (long)offset + limit);
            if (from < to)
            {
                _page.Append(value.AsSpan(from - start, to - from));
            }
            if (value.Length > 0)
            {
                _previousCharacter = value[^1];
            }
        }

        internal TextPage Complete()
        {
            if (offset > _total)
            {
                throw InvalidPage(
                    nameof(offset),
                    $"Offset {offset} exceeds the current result length {_total}. Results may have changed, or the offset may belong to another query. Restart at offset 0; then follow nextOffset with unchanged arguments until hasMore is false.",
                    offset,
                    limit,
                    _total,
                    0,
                    limit
                );
            }
            if (
                offset > 0
                && _characterAtOffset is { } first
                && char.IsLowSurrogate(first)
                && _characterBeforeOffset is { } before
                && char.IsHighSurrogate(before)
            )
            {
                throw InvalidPage(
                    nameof(offset),
                    "Offset splits a Unicode surrogate pair. Retry at the preceding complete character boundary.",
                    offset,
                    limit,
                    _total,
                    offset - 1,
                    Math.Max(limit, 2)
                );
            }
            if (
                _page.Length > 0
                && char.IsHighSurrogate(_page[^1])
                && offset + _page.Length < _total
            )
            {
                _page.Length--;
                if (_page.Length == 0)
                {
                    throw InvalidPage(
                        nameof(limit),
                        "Limit is too small for the Unicode character at this offset. Retry with a limit of at least 2 UTF-16 code units.",
                        offset,
                        limit,
                        _total,
                        offset,
                        2
                    );
                }
            }
            var content = _page.ToString();
            var nextOffset = offset + content.Length;
            var hasMore = nextOffset < _total;
            return new TextPage(
                content,
                offset,
                content.Length,
                _total,
                hasMore,
                hasMore ? nextOffset : null
            );
        }
    }
}
