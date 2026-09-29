using System.Text.Json.Serialization;
using Tandem.Infrastructure;

namespace Tandem.Advanced;

internal sealed record RetryHint(
    [property: JsonPropertyName("retryOffset")] int RetryOffset,
    [property: JsonPropertyName("retryLimit")] int RetryLimit
);

// One page of complete records bounded by count and by total characters; the first record always fits.
internal sealed class RecordPage
{
    internal const int MaximumLimit = 500;
    private const int MaximumCharacters = 64_000;
    private readonly int _offset;
    private readonly int _limit;
    private int _characters;

    internal RecordPage(int offset, int limit)
    {
        Validate(offset, limit);
        _offset = offset;
        _limit = limit;
    }

    internal int Count { get; private set; }

    internal int NextOffset => _offset + Count;

    internal static void Validate(int offset, int limit)
    {
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ToolInputException($"limit must be from 1 to {MaximumLimit}.");
        }
        if (offset < 0)
        {
            throw new PaginationValidationException(
                nameof(offset),
                "Offset cannot be negative. Retry at offset 0.",
                new RetryHint(0, limit)
            );
        }
    }

    internal bool TryAdd(int characters)
    {
        if (Count >= _limit || (Count > 0 && _characters + characters > MaximumCharacters))
        {
            return false;
        }
        Count++;
        _characters += characters;
        return true;
    }

    internal PaginationValidationException OffsetBeyondEnd(string records) =>
        new(
            "offset",
            $"Offset exceeds the {records} count. Restart at offset 0.",
            new RetryHint(0, _limit)
        );
}
