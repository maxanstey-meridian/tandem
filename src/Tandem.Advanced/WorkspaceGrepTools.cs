using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class WorkspaceGrepTools
{
    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
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
            "packages",
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
        var tools = options.Tools?.ToList() ?? [];
        if (tools.Any(tool => tool.Name == FileAccessProvider.GrepToolName))
        {
            throw new InvalidOperationException(
                $"Agent already exposes tool '{FileAccessProvider.GrepToolName}'."
            );
        }

        tools.Add(
            AIFunctionFactory.Create(
                (
                    [Description("Regular expression pattern (case-insensitive).")]
                        string regexPattern,
                    [Description("Optional repository-relative directory to search.")]
                        string directory = "",
                    [Description(
                        "Optional glob applied to repository-relative paths before files are opened."
                    )]
                        string? globPattern = null,
                    [Description("Whether to descend recursively.")] bool recursive = true,
                    [Description("Zero-based UTF-16 output offset.")] int offset = 0,
                    [Description("Maximum UTF-16 code units to return, from 1 to 65536.")]
                        int limit = BoundedTextPageReader.DefaultLimit,
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
                        cancellationToken
                    ),
                FileAccessProvider.GrepToolName,
                "Search workspace text files and return deterministic path:line:text records. Follow nextOffset until hasMore is false."
            )
        );
        options.Tools = tools;
    }

    internal static Task<TextPage> SearchAsync(
        string workspacePath,
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        int offset,
        int limit,
        CancellationToken cancellationToken = default,
        SearchDiagnostics? diagnostics = null
    )
    {
        var regex = new Regex(
            regexPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            _regexTimeout
        );
        Regex? glob = string.IsNullOrEmpty(globPattern) ? null : GlobRegex(globPattern);
        var root = WorkspacePathAuthority.Resolve(
            workspacePath,
            string.IsNullOrEmpty(directory) ? "." : directory,
            "search"
        );
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Search directory does not exist: {directory}");
        }
        return BoundedTextPageReader.ReadAsync(
            SearchDirectoryAsync(
                Path.GetFullPath(workspacePath),
                root,
                regex,
                glob,
                recursive,
                diagnostics,
                cancellationToken
            ),
            offset,
            limit,
            cancellationToken
        );
    }

    private static async IAsyncEnumerable<string> SearchDirectoryAsync(
        string workspaceRoot,
        string directory,
        Regex regex,
        Regex? glob,
        bool recursive,
        SearchDiagnostics? diagnostics,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics?.DirectoryEnumerated?.Invoke(
            Path.GetRelativePath(workspaceRoot, directory).Replace('\\', '/')
        );
        var entries = Directory.GetFileSystemEntries(directory);
        Array.Sort(entries, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                var name = Path.GetFileName(entry);
                if (recursive && !IsExcludedDirectory(name))
                {
                    await foreach (
                        var record in SearchDirectoryAsync(
                            workspaceRoot,
                            entry,
                            regex,
                            glob,
                            true,
                            diagnostics,
                            cancellationToken
                        )
                    )
                    {
                        yield return record;
                    }
                }
                continue;
            }
            var relative = Path.GetRelativePath(workspaceRoot, entry).Replace('\\', '/');
            if (
                _binaryExtensions.Contains(Path.GetExtension(entry))
                || glob is not null && !glob.IsMatch(relative)
            )
            {
                continue;
            }
            await foreach (
                var record in SearchFileAsync(
                    entry,
                    relative,
                    regex,
                    diagnostics,
                    cancellationToken
                )
            )
            {
                yield return record;
            }
        }
    }

    private static bool IsExcludedDirectory(string name) =>
        _excludedDirectories.Contains(name)
        || name.StartsWith("bazel-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase);

    private static async IAsyncEnumerable<string> SearchFileAsync(
        string path,
        string relative,
        Regex regex,
        SearchDiagnostics? diagnostics,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        diagnostics?.FileOpened?.Invoke(relative);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var probe = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            var count = await stream.ReadAsync(probe.AsMemory(0, 8192), cancellationToken);
            if (LooksBinary(probe.AsSpan(0, count)))
            {
                yield break;
            }
            stream.Position = 0;
            diagnostics?.TextDecodingStarted?.Invoke(relative);
            using var reader = new StreamReader(stream, _strictUtf8, true, 4096, false);
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lineNumber++;
                if (regex.IsMatch(line))
                {
                    yield return $"{relative}:{lineNumber}:{line}\n";
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe);
        }
    }

    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var suspicious = 0;
        foreach (var value in bytes)
        {
            if (value == 0)
            {
                return true;
            }

            if (value < 32 && value is not (9 or 10 or 13 or 12 or 8))
            {
                suspicious++;
            }
        }
        return bytes.Length > 0 && suspicious >= 4 && suspicious * 100 >= bytes.Length;
    }

    private static Regex GlobRegex(string glob)
    {
        var pattern = new StringBuilder("\\A");
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
