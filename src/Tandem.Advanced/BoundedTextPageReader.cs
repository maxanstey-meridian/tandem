using System.Buffers;
using System.Text;
using System.Text.Json.Serialization;

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
);

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
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Offset {offset} is beyond the text length {position}."
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
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "Offset cannot split a Unicode surrogate pair."
            );
        }

        if (page.Length > 0 && char.IsHighSurrogate(page[^1]) && offset + page.Length < position)
        {
            page.Length--;
            if (page.Length == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(limit),
                    "Limit is too small to return the Unicode character at this offset."
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

    private static void ValidateBounds(int offset, int limit)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        }
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Limit must be from 1 to {MaximumLimit}."
            );
        }
    }

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
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    $"Offset {offset} is beyond the text length {_total}."
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
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    "Offset cannot split a Unicode surrogate pair."
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
                    throw new ArgumentOutOfRangeException(
                        nameof(limit),
                        "Limit is too small to return the Unicode character at this offset."
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
