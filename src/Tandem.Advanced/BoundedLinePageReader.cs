using Tandem.Infrastructure;

namespace Tandem.Advanced;

internal static class BoundedLinePageReader
{
    internal const int MaximumCharacters = 65536;

    private sealed record Continuation(
        TextFileVersion Version,
        long Position,
        int Line,
        bool Fragment
    );

    internal static Task<object> ReadAsync(
        string path,
        int startLine = 1,
        int lineCount = 200,
        CancellationToken cancellationToken = default,
        string? cursor = null
    )
    {
        if (startLine < 1 || lineCount is < 1 or > 2000)
        {
            throw new PaginationValidationException(
                nameof(startLine),
                "startLine must be at least 1; lineCount must be from 1 to 2000.",
                new
                {
                    retryStartLine = Math.Max(1, startLine),
                    retryLineCount = Math.Clamp(lineCount, 1, 2000),
                }
            );
        }

        if (cursor is not null && startLine != 1)
        {
            throw new ToolInputException(
                "Use cursor alone with path and lineCount to continue; omit startLine."
            );
        }

        var scope = ToolCursor.Scope("read", Path.GetFullPath(path));
        var continuation = cursor is null ? null : ToolCursor.Decode<Continuation>(cursor, scope);
        if (
            continuation is not null
            && (continuation.Version is null || continuation.Position < 0 || continuation.Line < 1)
        )
        {
            throw new ToolInputException("Invalid file continuation. Restart without cursor.");
        }
        continuation?.Version.Validate(path);
        var version = TextFileVersion.Read(path);
        using var reader = new PositionedTextReader(
            path,
            continuation?.Position,
            cancellationToken
        );
        var line = continuation?.Line ?? 1;
        if (line < 1)
        {
            throw new ToolInputException("Invalid line continuation.");
        }

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

        var characters = 0;
        var fragments = new List<object>();
        var returned = 0;
        var inFragment = continuation?.Fragment ?? false;
        var firstLine = line;
        while (!reader.End && returned < lineCount && characters < MaximumCharacters - 64)
        {
            var part = reader.Fragment(MaximumCharacters - characters - 64);
            characters += part.Text.Length + 64;
            fragments.Add(
                new
                {
                    line,
                    text = part.Text,
                    continuesPrevious = inFragment,
                    lineComplete = part.LineEnded,
                }
            );
            returned++;
            inFragment = !part.LineEnded;
            if (part.LineEnded)
            {
                line++;
            }
        }
        version.Validate(path);
        var hasMore = !reader.End;
        return Task.FromResult<object>(
            new
            {
                startLine = firstLine,
                returnedLines = returned,
                // Line numbers remain attached to every fragment.
                lines = fragments,
                hasMore,
                nextCursor = hasMore
                    ? ToolCursor.Encode(
                        scope,
                        new Continuation(version, reader.Position, line, inFragment)
                    )
                    : null,
                totalLines = hasMore ? (int?)null : line - 1,
                pagination = "Copy nextCursor as cursor with the same path; lineCount may change between pages. Omit startLine. A continued fragment belongs to the same source line. Restart after edits.",
            }
        );
    }
}
