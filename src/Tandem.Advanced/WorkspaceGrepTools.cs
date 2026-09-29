using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class WorkspaceGrepTools
{
    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);

    internal static AIFunction Create(string workspacePath) =>
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
                [Description("Maximum matching records, 1 to 500. Output is also size bounded.")]
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
                + "By default skip binary files, symlinks and Git-ignored files (outside a Git repository, common build and dependency directories); skipped files are reported in the result. "
                + "An explicit directory or path prefix overrides those exclusions, never Git metadata or link boundaries. "
                + "Incomplete oversized matches can be read with file_access_read at their line."
        );

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

    internal static async Task<GrepPage> SearchAsync(
        string workspacePath,
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default,
        bool includeExcluded = false,
        bool literal = false,
        bool caseSensitive = false
    )
    {
        var page = new RecordPage(offset, limit);
        var regex = new Regex(
            literal ? Regex.Escape(regexPattern) : regexPattern,
            RegexOptions.CultureInvariant
                | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
            _regexTimeout
        );
        var glob = string.IsNullOrEmpty(globPattern) ? null : CreateMatcher(globPattern);
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
        var matchIndex = 0;
        var candidates = await CandidatesAsync(
            workspace,
            root,
            globPattern,
            recursive,
            includeExcluded,
            cancellationToken
        );
        foreach (var relative in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (glob is not null && !glob.Match(relative).HasMatches)
            {
                continue;
            }
            var path = Path.Combine(workspace, relative);
            PositionedTextReader? reader = null;
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Skip(relative, "symbolic link");
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    continue;
                }
                if (WorkspaceSearchPolicy.HasBinaryExtension(relative))
                {
                    Skip(relative, "binary extension");
                    continue;
                }
                reader = new PositionedTextReader(path, cancellationToken);
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
                            if (!page.TryAdd(relative.Length + excerpt.Length + 32))
                            {
                                return new GrepPage(
                                    matches,
                                    skippedCount,
                                    skipped,
                                    page.NextOffset
                                );
                            }
                            matches.Add(new Match(relative, line, excerpt));
                        }
                        matchIndex++;
                    }
                    line++;
                }
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Tracked files deleted from the working tree are listed by Git but have nothing to search.
            }
            catch (Exception e)
                when (e
                        is DecoderFallbackException
                            or InvalidDataException
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
        return new GrepPage(matches, skippedCount, skipped, null);
    }

    // Candidate files in deterministic ordinal order. Inside a Git work tree the listing honours
    // .gitignore; elsewhere, or when exclusions are lifted or an ignored path is named explicitly,
    // the file system is walked with the performance prune list.
    private static async Task<IReadOnlyList<string>> CandidatesAsync(
        string workspace,
        string root,
        string? globPattern,
        bool recursive,
        bool includeExcluded,
        CancellationToken cancellationToken
    )
    {
        var rootRelative = Relative(workspace, root);
        var rootSegments = Segments(rootRelative);
        var prefix = LiteralPrefix(globPattern);
        if (!Compatible(rootSegments, prefix))
        {
            return [];
        }
        var explicitPath =
            recursive && prefix.Length > rootSegments.Length
                ? string.Join('/', prefix)
                : rootRelative;
        if (!includeExcluded)
        {
            var listed = await GitListAsync(
                workspace,
                rootRelative,
                explicitPath,
                cancellationToken
            );
            if (listed is not null)
            {
                return recursive
                    ? listed
                    : [.. listed.Where(path => ParentOf(path) == rootRelative)];
            }
        }
        return Walk(workspace, root, prefix, recursive, includeExcluded);
    }

    private static async Task<IReadOnlyList<string>?> GitListAsync(
        string workspace,
        string root,
        string explicitPath,
        CancellationToken cancellationToken
    )
    {
        // Exit code 1 means inside a work tree and not ignored; 0 (explicitly named ignored path)
        // and 128 (no work tree) fall back to the walk.
        var ignored = await GitProcess.RunAsync(
            workspace,
            ["check-ignore", "-q", "--", explicitPath.Length == 0 ? "." : explicitPath],
            64 * 1024,
            cancellationToken
        );
        if (ignored.ExitCode != 1)
        {
            return null;
        }
        var listed = await GitProcess.RunAsync(
            workspace,
            [
                "--literal-pathspecs",
                "ls-files",
                "-z",
                "--cached",
                "--others",
                "--exclude-standard",
                "--",
                root.Length == 0 ? "." : root,
            ],
            16 * 1024 * 1024,
            cancellationToken
        );
        return listed.ExitCode != 0 || listed.TimedOut || listed.StdoutTruncated
            ? null
            :
            [
                .. listed
                    .Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];
    }

    private static string[] Walk(
        string workspace,
        string root,
        string[] prefix,
        bool recursive,
        bool includeExcluded
    ) =>
        [
            .. new FileSystemEnumerable<string>(
                root,
                (ref FileSystemEntry entry) => Relative(workspace, entry.ToFullPath()),
                new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    AttributesToSkip = 0,
                    IgnoreInaccessible = true,
                }
            )
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !IsGitMetadata(entry.FileName)
                    && (
                        !entry.IsDirectory || (entry.Attributes & FileAttributes.ReparsePoint) != 0
                    ),
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    if (
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0
                        || IsGitMetadata(entry.FileName)
                    )
                    {
                        return false;
                    }
                    var segments = Segments(Relative(workspace, entry.ToFullPath()));
                    return Compatible(segments, prefix)
                        && (
                            includeExcluded
                            || segments.Length <= prefix.Length
                            || !WorkspaceSearchPolicy.IsExcludedDirectory(entry.FileName.ToString())
                        );
                },
            }.Order(StringComparer.Ordinal),
        ];

    private static Matcher CreateMatcher(string glob)
    {
        // Slashless globs match filenames at every depth; path globs are repository-relative.
        var normalized = glob.Replace('\\', '/');
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(normalized.Contains('/') ? normalized : "**/" + normalized);
        return matcher;
    }

    // Literal leading directories of a path glob are an explicit selection: they bound the walk and
    // override the performance prune list, never Git metadata or link boundaries.
    private static string[] LiteralPrefix(string? glob) =>
        glob is null || !glob.Replace('\\', '/').Contains('/')
            ? []
            :
            [
                .. glob.Replace('\\', '/')
                    .Split('/')
                    .TakeWhile(segment =>
                        segment.Length > 0 && segment is not ("." or "..") && !segment.Contains('*')
                    ),
            ];

    private static bool Compatible(string[] segments, string[] prefix)
    {
        for (var i = 0; i < Math.Min(segments.Length, prefix.Length); i++)
        {
            if (!string.Equals(segments[i], prefix[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsGitMetadata(ReadOnlySpan<char> name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string workspace, string path)
    {
        var relative = Path.GetRelativePath(workspace, path).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    private static string[] Segments(string relative) =>
        relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string ParentOf(string relative) =>
        relative.LastIndexOf('/') is var index and >= 0 ? relative[..index] : "";
}
