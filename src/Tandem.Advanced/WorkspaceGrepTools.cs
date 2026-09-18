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
    private static readonly HashSet<string> _excludedDirectories = new(
        [
            ".angular",
            ".build",
            ".bundle",
            ".cache",
            ".dart_tool",
            ".eggs",
            ".expo",
            ".git",
            ".gradle",
            ".hg",
            ".idea",
            ".mypy_cache",
            ".next",
            ".nox",
            ".nuxt",
            ".nx",
            ".nyc_output",
            ".output",
            ".parcel-cache",
            ".pytest_cache",
            ".pnpm-store",
            ".ruff_cache",
            ".sass-cache",
            ".serverless",
            ".stack-work",
            ".svelte-kit",
            ".svn",
            ".tox",
            ".turbo",
            ".terraform",
            ".terragrunt-cache",
            ".venv",
            ".vite",
            ".vs",
            ".yarn",
            "_build",
            "__pycache__",
            "artifacts",
            "bin",
            "bower_components",
            "Binaries",
            "build",
            "coverage",
            "Carthage",
            "CMakeFiles",
            "deps",
            "DerivedData",
            "DerivedDataCache",
            "dist",
            "env",
            "jspm_packages",
            "Intermediate",
            "Library",
            "node_modules",
            "obj",
            "out",
            "Pods",
            "Saved",
            "site-packages",
            "target",
            "TestResults",
            "tmp",
            "storybook-static",
            "venv",
            "vendor",
        ],
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> _binaryExtensions = new(
        [
            ".7z",
            ".a",
            ".apk",
            ".avi",
            ".bin",
            ".bmp",
            ".bz2",
            ".class",
            ".db",
            ".deb",
            ".dmg",
            ".dll",
            ".dylib",
            ".ear",
            ".exe",
            ".flac",
            ".gif",
            ".gem",
            ".gz",
            ".ico",
            ".ipa",
            ".iso",
            ".jar",
            ".jpeg",
            ".jpg",
            ".mov",
            ".mp3",
            ".mp4",
            ".o",
            ".otf",
            ".nupkg",
            ".pdf",
            ".pdb",
            ".png",
            ".pyc",
            ".pyo",
            ".rar",
            ".rpm",
            ".so",
            ".sqlite",
            ".sqlite3",
            ".snupkg",
            ".tar",
            ".tgz",
            ".ttf",
            ".war",
            ".wasm",
            ".wav",
            ".webm",
            ".webp",
            ".whl",
            ".woff",
            ".woff2",
            ".xz",
            ".zip",
        ],
        StringComparer.OrdinalIgnoreCase
    );

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
                    [Description("Copy nextCursor with unchanged search arguments.")]
                        string? cursor = null,
                    [Description(
                        "Search normally excluded build/dependency directories; Git metadata and links stay excluded."
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
                        cursor,
                        limit,
                        cancellationToken,
                        includeExcluded: includeExcluded,
                        literal: literal,
                        caseSensitive: caseSensitive
                    ),
                FileAccessProvider.GrepToolName,
                "Search text files, returning path/line/text records. Follow nextCursor; no total scan for counts. "
                    + "By default skip binary files, symlinks and build/dependency/cache directories (including "
                    + string.Join(", ", _excludedDirectories.Order())
                    + "; bazel-*; cmake-build-*). Explicit path prefixes override performance exclusions, never .git/link boundaries. "
                    + "Skipped files are reported. Incomplete oversized matches can be read with file_access_read at their line."
            )
        );
    }

    internal sealed record Match(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("incomplete")] bool Incomplete = false
    );

    internal sealed record GrepPage(
        [property: JsonPropertyName("matches")] IReadOnlyList<Match> Matches,
        [property: JsonPropertyName("nextCursor")] string? NextCursor,
        [property: JsonPropertyName("skippedCount")] int SkippedCount,
        [property: JsonPropertyName("skipped")] IReadOnlyList<string> Skipped,
        [property: JsonPropertyName("exclusionsApplied")] bool ExclusionsApplied
    )
    {
        [JsonPropertyName("hasMore")]
        public bool HasMore => NextCursor is not null;

        [JsonPropertyName("returnedCount")]
        public int ReturnedCount => Matches.Count;

        [System.Text.Json.Serialization.JsonIgnore]
        public string Content =>
            string.Concat(Matches.Select(m => $"{m.Path}:{m.Line}:{m.Text}\n"));
    }

    private sealed record Continuation(
        string Path,
        long Position,
        int Line,
        TextFileVersion Version
    );

    internal static Task<GrepPage> SearchAsync(
        string workspacePath,
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        string? cursor = null,
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

        var scope = ToolCursor.Scope(
            "grep",
            workspace,
            root,
            regexPattern,
            globPattern,
            recursive,
            limit,
            includeExcluded,
            literal,
            caseSensitive
        );
        var resume = cursor is null ? null : ToolCursor.Decode<Continuation>(cursor, scope);
        if (resume is not null)
        {
            if (
                resume.Line < 1
                || resume.Position < 0
                || resume.Version is null
                || string.IsNullOrWhiteSpace(resume.Path)
            )
            {
                throw new Tandem.Infrastructure.ToolInputException("Invalid search continuation.");
            }

            resume.Version.Validate(
                WorkspacePathAuthority.Resolve(workspace, resume.Path, "search")
            );
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
        foreach (
            var path in SearchFiles(
                workspace,
                root,
                LiteralPathPrefix(globPattern),
                recursive,
                includeExcluded,
                resume?.Path,
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

            if (_binaryExtensions.Contains(Path.GetExtension(path)))
            {
                Skip(relative, "binary extension");
                continue;
            }
            PositionedTextReader? reader = null;
            try
            {
                diagnostics?.FileOpened?.Invoke(relative);
                var version = TextFileVersion.Read(path);
                reader = new PositionedTextReader(
                    path,
                    resume?.Path == relative ? resume.Position : null,
                    cancellationToken
                );
                diagnostics?.TextDecodingStarted?.Invoke(relative);
                var line = resume?.Path == relative ? resume.Line : 1;
                while (!reader.End)
                {
                    var position = reader.Position;
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
                        // A single oversized match remains identifiable and fully readable by line.
                        var excerpt = part.Text.Length > 32000 ? part.Text[..32000] : part.Text;
                        if (excerpt.Length > 0 && char.IsHighSurrogate(excerpt[^1]))
                        {
                            excerpt = excerpt[..^1];
                        }

                        if (
                            matches.Count == limit
                            || characters + relative.Length + excerpt.Length + 32 > 64000
                        )
                        {
                            version.Validate(path);
                            return Task.FromResult(
                                new GrepPage(
                                    matches,
                                    ToolCursor.Encode(
                                        scope,
                                        new Continuation(relative, position, line, version)
                                    ),
                                    skippedCount,
                                    skipped,
                                    !includeExcluded
                                )
                            );
                        }
                        matches.Add(
                            new Match(relative, line, excerpt, excerpt.Length != part.Text.Length)
                        );
                        characters += relative.Length + excerpt.Length + 32;
                    }
                    line++;
                }
                version.Validate(path);
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
        return Task.FromResult(
            new GrepPage(matches, null, skippedCount, skipped, !includeExcluded)
        );
    }

    private static IEnumerable<string> SearchFiles(
        string workspace,
        string directory,
        string[] prefix,
        bool recursive,
        bool includeExcluded,
        string? after,
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
            if (
                after is not null
                && ComparePaths(rel, after) < 0
                && !after.StartsWith(rel + "/", StringComparison.Ordinal)
            )
            {
                continue;
            }

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
                        || !IsExcludedDirectory(Path.GetFileName(entry))
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
                            after,
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

    private static int ComparePaths(string left, string right)
    {
        var a = left.Split('/');
        var b = right.Split('/');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var comparison = StringComparer.Ordinal.Compare(a[i], b[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return a.Length.CompareTo(b.Length);
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

    private static bool IsExcludedDirectory(string name) =>
        _excludedDirectories.Contains(name)
        || name.StartsWith("bazel-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase);

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
