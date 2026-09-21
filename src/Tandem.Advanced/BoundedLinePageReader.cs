using System.Text.Json.Serialization;
using Tandem.Infrastructure;

namespace Tandem.Advanced;

internal sealed record LinePage(
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("lines")] IReadOnlyList<string> Lines,
    [property:
        JsonPropertyName("nextStartLine"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)
    ]
        int? NextStartLine,
    [property:
        JsonPropertyName("nextCharacterOffset"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)
    ]
        int? NextCharacterOffset
);

internal static class BoundedLinePageReader
{
    internal const int MaximumCharacters = 65536;

    internal static Task<LinePage> ReadAsync(
        string path,
        int startLine = 1,
        int lineCount = 200,
        int characterOffset = 0,
        CancellationToken cancellationToken = default
    )
    {
        if (startLine < 1 || lineCount is < 1 or > 2000 || characterOffset < 0)
        {
            throw new PaginationValidationException(
                nameof(startLine),
                "startLine must be at least 1; lineCount must be from 1 to 2000; characterOffset cannot be negative.",
                new
                {
                    retryStartLine = Math.Max(1, startLine),
                    retryLineCount = Math.Clamp(lineCount, 1, 2000),
                    retryCharacterOffset = Math.Max(0, characterOffset),
                }
            );
        }

        using var reader = new PositionedTextReader(path, cancellationToken);
        var line = 1;
        while (line < startLine && !reader.End)
        {
            if (reader.Fragment(4096).LineEnded)
            {
                line++;
            }
        }
        if (line < startLine)
        {
            throw new PaginationValidationException(
                nameof(startLine),
                "startLine exceeds the file. Restart at line 1.",
                new { retryStartLine = 1 }
            );
        }

        var lines = new List<string>();
        var characters = 0;
        var pending = "";
        var consumedInLine = 0;
        var firstLine = line;
        if (characterOffset > 0)
        {
            var crossed = false;
            var skipped = 0;
            while (!reader.End)
            {
                var part = reader.Fragment(MaximumCharacters - 64);
                skipped += part.Text.Length;
                if (skipped >= characterOffset)
                {
                    crossed = true;
                    var index = part.Text.Length - (skipped - characterOffset);
                    if (index > 0 && char.IsHighSurrogate(part.Text[index - 1]))
                    {
                        throw new PaginationValidationException(
                            nameof(characterOffset),
                            "characterOffset splits a Unicode surrogate pair. Retry at the preceding complete character boundary.",
                            new
                            {
                                retryStartLine = startLine,
                                retryCharacterOffset = characterOffset - 1,
                            }
                        );
                    }

                    pending = part.Text[index..];
                    if (part.LineEnded)
                    {
                        if (pending.Length > 0)
                        {
                            lines.Add(pending);
                            characters += pending.Length + 64;
                        }
                        else
                        {
                            firstLine = line + 1;
                        }
                        pending = "";
                        line++;
                    }
                    else
                    {
                        characters = pending.Length;
                        consumedInLine = characterOffset + pending.Length;
                    }
                    break;
                }
                if (part.LineEnded)
                {
                    break;
                }
            }
            if (!crossed)
            {
                throw new PaginationValidationException(
                    nameof(characterOffset),
                    "characterOffset exceeds the line. Restart the line at offset 0.",
                    new { retryStartLine = startLine, retryCharacterOffset = 0 }
                );
            }
        }

        while (!reader.End && lines.Count < lineCount && characters < MaximumCharacters - 64)
        {
            var part = reader.Fragment(MaximumCharacters - characters - 64);
            pending += part.Text;
            characters += part.Text.Length + 64;
            consumedInLine += part.Text.Length;
            if (part.LineEnded)
            {
                lines.Add(pending);
                pending = "";
                consumedInLine = 0;
                line++;
            }
        }
        if (pending.Length > 0)
        {
            lines.Add(pending);
        }

        var hasMore = !reader.End;
        return Task.FromResult(
            new LinePage(
                firstLine,
                lines,
                hasMore ? line : null,
                hasMore && pending.Length > 0 ? consumedInLine : null
            )
        );
    }
}
