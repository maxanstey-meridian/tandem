using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class WorkspaceGrepTools
{
    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);

    internal static void Add(ChatOptions options, string workspacePath)
    {
        options.Tools ??= [];
        options.Tools.Add(
            AIFunctionFactory.Create(
                (
                    [Description("Pattern; regular expression by default.")] string regexPattern,
                    [Description(
                        "Repository-relative directory; explicitly named directories are searched."
                    )]
                        string directory = "",
                    [Description(
                        "Slashless glob matches filenames; path globs are repository-relative."
                    )]
                        string? globPattern = null,
                    bool recursive = true,
                    [Description(
                        "Maximum matching records, 1 to 500. Output is also size bounded."
                    )]
                        int limit = 100,
                    [Description(
                        "Zero-based match offset; continue a previous page with the returned nextOffset."
                    )]
                        int offset = 0,
                    [Description(
                        "Search normally excluded directories; Git metadata and links stay excluded."
                    )]
                        bool includeExcluded = false,
                    [Description("Treat regexPattern as literal text.")] bool literal = false,
                    [Description("Case-sensitive matching; default is case-insensitive.")]
                        bool caseSensitive = false,
                    CancellationToken cancellationToken = default
                ) =>
                    SearchAsync(
                        workspacePath,
                        directory,
                        regexPattern,
                        globPattern,
                        recursive,
                        offset,
                        limit,
                        cancellationToken,
                        includeExcluded: includeExcluded,
                        literal: literal,
                        caseSensitive: caseSensitive
                    ),
                FileAccessProvider.GrepToolName,
                "Search text files, returning path/line/text records. Continue with the returned nextOffset; there is no total count scan. "
                    + "By default skip binary files, symlinks and performance-excluded directories; skipped files are reported in the result. "
                    + "Explicit path prefixes override performance exclusions, never Git metadata or link boundaries. "
                    + "Incomplete oversized matches can be read with file_access_read at their line."
            )
        );
    }

    internal sealed record Match(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("text")] string Text
    );

    internal sealed record GrepPage(
        [property: JsonPropertyName("matches")] IReadOnlyList<Match> Matches,
        [property: JsonPropertyName("skippedCount")] int SkippedCount,
        [property: JsonPropertyName("skipped")] IReadOnlyList<string> Skipped,
        [property:
            JsonPropertyName("nextOffset"),
            JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)
        ]
            int? NextOffset
    )
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public string Content =>
            string.Concat(Matches.Select(m => $"{m.Path}:{m.Line}:{m.Text}\n"));
    }

    internal static Task<GrepPage> SearchAsync(
        string workspacePath,
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default,
        SearchDiagnostics? diagnostics = null,
        bool includeExcluded = false,
        bool literal = false,
        bool caseSensitive = false
    )
    {
        if (limit is < 1 or > 500)
        {
            throw new Tandem.Infrastructure.ToolInputException(
                "limit must be from 1 to 500 matching records."
            );
        }
        if (offset < 0)
        {
            throw new Tandem.Infrastructure.PaginationValidationException(
                nameof(offset),
                "Offset cannot be negative. Retry at offset 0.",
                new { retryOffset = 0, retryLimit = Math.Clamp(limit, 1, 500) }
            );
        }

        var regex = new Regex(
            literal ? Regex.Escape(regexPattern) : regexPattern,
            RegexOptions.CultureInvariant
                | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
            _regexTimeout
        );
        var glob = string.IsNullOrEmpty(globPattern) ? null : GlobRegex(globPattern);
        var workspace = Path.GetFullPath(workspacePath);
        var root = WorkspacePathAuthority.Resolve(
            workspace,
            string.IsNullOrEmpty(directory) ? "." : directory,
            "search"
        );
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Search directory does not exist: {directory}");
        }

        var skipped = new List<string>();
        var skippedCount = 0;
        void Skip(string path, string reason)
        {
            skippedCount++;
            if (skipped.Count < 20)
            {
                skipped.Add($"{path}: {reason}");
            }
        }
        var matches = new List<Match>();
        var characters = 0;
        var matchIndex = 0;
        foreach (
            var path in SearchFiles(
                workspace,
                root,
                LiteralPathPrefix(globPattern),
                recursive,
                includeExcluded,
                Skip,
                diagnostics,
                cancellationToken
            )
        )
        {
            var relative = Path.GetRelativePath(workspace, path).Replace('\\', '/');
            if (glob is not null && !glob.IsMatch(relative))
            {
                continue;
            }

            if (WorkspaceSearchPolicy.HasBinaryExtension(relative))
            {
                Skip(relative, "binary extension");
                continue;
            }
            PositionedTextReader? reader = null;
            try
            {
                diagnostics?.FileOpened?.Invoke(relative);
                reader = new PositionedTextReader(path, cancellationToken);
                diagnostics?.TextDecodingStarted?.Invoke(relative);
                var line = 1;
                while (!reader.End)
                {
                    var part = reader.Fragment(1024 * 1024);
                    if (!part.LineEnded)
                    {
                        Skip(
                            $"{relative}:{line}",
                            "line exceeds 1 MiB search bound; use file_access_read to inspect it"
                        );
                        while (!part.LineEnded)
                        {
                            part = reader.Fragment(4096);
                        }

                        line++;
                        continue;
                    }
                    if (regex.IsMatch(part.Text))
                    {
                        if (matchIndex >= offset)
                        {
                            // A single oversized match remains identifiable and fully readable by line.
                            var excerpt = part.Text.Length > 32000 ? part.Text[..32000] : part.Text;
                            var truncated = excerpt.Length != part.Text.Length;
                            if (excerpt.Length > 0 && char.IsHighSurrogate(excerpt[^1]))
                            {
                                excerpt = excerpt[..^1];
                            }

                            if (truncated)
                            {
                                excerpt += "…";
                            }
                            if (
                                matches.Count == limit
                                || (
                                    matches.Count > 0
                                    && characters + relative.Length + excerpt.Length + 32 > 64000
                                )
                            )
                            {
                                return Task.FromResult(
                                    new GrepPage(
                                        matches,
                                        skippedCount,
                                        skipped,
                                        offset + matches.Count
                                    )
                                );
                            }
                            matches.Add(new Match(relative, line, excerpt));
                            characters += relative.Length + excerpt.Length + 32;
                        }
                        matchIndex++;
                    }
                    line++;
                }
            }
            catch (Exception e)
                when (e
                        is DecoderFallbackException
                            or InvalidDataException
                            or FileNotFoundException
                            or DirectoryNotFoundException
                            or UnauthorizedAccessException
                )
            {
                Skip(relative, e.Message);
            }
            finally
            {
                reader?.Dispose();
            }
        }
        return Task.FromResult(new GrepPage(matches, skippedCount, skipped, null));
    }

    private static IEnumerable<string> SearchFiles(
        string workspace,
        string directory,
        string[] prefix,
        bool recursive,
        bool includeExcluded,
        Action<string, string> skip,
        SearchDiagnostics? diagnostics,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relative = Path.GetRelativePath(workspace, directory).Replace('\\', '/');
        var components = relative == "." ? [] : relative.Split('/');
        for (var i = 0; i < Math.Min(components.Length, prefix.Length); i++)
        {
            if (!string.Equals(components[i], prefix[i], StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }
        }

        diagnostics?.DirectoryEnumerated?.Invoke(relative);
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(
                directory,
                components.Length < prefix.Length ? prefix[components.Length] : "*",
                new EnumerationOptions
                {
                    MatchType = MatchType.Simple,
                    MatchCasing = MatchCasing.CaseInsensitive,
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                }
            );
            Array.Sort(entries, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            skip(relative, e.Message);
            yield break;
        }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(workspace, entry).Replace('\\', '/');
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception e)
                when (e
                        is FileNotFoundException
                            or DirectoryNotFoundException
                            or UnauthorizedAccessException
                )
            {
                skip(rel, e.Message);
                continue;
            }
            if (string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                skip(rel, "symbolic link");
                continue;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (
                    recursive
                    && (
                        includeExcluded
                        || components.Length < prefix.Length
                        || !WorkspaceSearchPolicy.IsExcludedDirectory(Path.GetFileName(entry))
                    )
                )
                {
                    foreach (
                        var file in SearchFiles(
                            workspace,
                            entry,
                            prefix,
                            true,
                            includeExcluded,
                            skip,
                            diagnostics,
                            cancellationToken
                        )
                    )
                    {
                        yield return file;
                    }
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    private static string[] LiteralPathPrefix(string? glob)
    {
        if (string.IsNullOrEmpty(glob) || (!glob.Contains('/') && !glob.Contains('\\')))
        {
            return [];
        }
        // Slashless globs match filenames at every depth. Path globs are repository-relative.
        return glob.Replace('\\', '/')
            .Split('/')
            .TakeWhile(component =>
                component.Length > 0
                && component is not ("." or "..")
                && component.IndexOfAny(['*', '?']) < 0
            )
            .ToArray();
    }

    private static Regex GlobRegex(string glob)
    {
        var pattern = new StringBuilder("\\A");
        if (!glob.Contains('/') && !glob.Contains('\\'))
        {
            pattern.Append("(?:.*/)?");
        }
        for (var i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        pattern.Append("(?:.*/)?");
                        i++;
                    }
                    else
                    {
                        pattern.Append(".*");
                    }
                }
                else
                {
                    pattern.Append("[^/]*");
                }
            }
            else if (glob[i] == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(glob[i] == '\\' ? "/" : glob[i].ToString()));
            }
        }
        pattern.Append("\\z");
        return new Regex(
            pattern.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            _regexTimeout
        );
    }

    internal sealed record SearchDiagnostics(
        Action<string>? DirectoryEnumerated = null,
        Action<string>? FileOpened = null,
        Action<string>? TextDecodingStarted = null
    );
}
