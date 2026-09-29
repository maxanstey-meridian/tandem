using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

namespace Tandem.Advanced;

internal static class ReadOnlyGitTools
{
    internal const string ChangedFilesToolName = "git_changed_files";
    internal const string DiffToolName = "git_diff";
    internal const string StatusToolName = "git_status";
    internal const string LogToolName = "git_log";
    internal const string ShowToolName = "git_show";
    internal const string BlameToolName = "git_blame";
    internal const string CompareToolName = "git_compare";
    internal const string CompareDescription =
        "Compare exact revisions when the diff is relevant, optionally restricted to one changed path. Use path filtering and pagination as needed; do not treat a sampled diff as complete evidence for repository-wide claims.";

    internal static void Add(
        ChatOptions options,
        string workspacePath,
        ToolEffectRegistry toolEffects
    )
    {
        var repository = new ReadOnlyGitRepository(workspacePath);
        var tools = new AITool[]
        {
            AIFunctionFactory.Create(
                repository.StatusAsync,
                StatusToolName,
                "Inspect staged, unstaged and untracked changes as complete records. Continue with the returned nextOffset; results are recomputed, so restart after workspace changes."
            ),
            AIFunctionFactory.Create(
                repository.WorkspaceDiffAsync,
                DiffToolName,
                "Read a bounded staged or unstaged workspace diff page with plain integer offset/limit pagination; follow the returned nextOffset, optionally restricted to one repository-relative path."
            ),
            AIFunctionFactory.Create(
                repository.LogAsync,
                LogToolName,
                "Read bounded Git history with stable machine-readable commit formatting."
            ),
            AIFunctionFactory.Create(
                repository.ShowAsync,
                ShowToolName,
                "Read a bounded commit and patch page for one exact Git revision with plain integer offset/limit pagination; follow the returned nextOffset, optionally restricted to one path."
            ),
            AIFunctionFactory.Create(
                repository.BlameAsync,
                BlameToolName,
                "Read porcelain line attribution for one repository-relative text file with plain integer offset/limit pagination; follow the returned nextOffset, optionally restricted to a line range."
            ),
            AIFunctionFactory.Create(
                repository.ChangedFilesAsync,
                ChangedFilesToolName,
                "List every path changed between two exact Git revisions, including additions, deletions, and renames. Page through with plain integer offset/limit and the returned nextOffset."
            ),
            AIFunctionFactory.Create(repository.CompareAsync, CompareToolName, CompareDescription),
        };
        foreach (var tool in tools)
        {
            HarnessTools.AddBuiltIn(options, toolEffects, tool);
        }
    }
}

// Repository-configured hooks such as core.fsmonitor must never run during read-only inspection.
internal static class GitProcess
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private static readonly Dictionary<string, string> _environment = new()
    {
        ["GIT_PAGER"] = "cat",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    internal static Task<LocalProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int maximumOutputBytes,
        CancellationToken cancellationToken
    ) =>
        LocalProcess.RunAsync(
            new LocalProcessRequest(
                "git",
                ["-c", "core.fsmonitor=false", .. arguments],
                workingDirectory,
                _timeout,
                maximumOutputBytes,
                _environment
            ),
            cancellationToken
        );
}

internal sealed record GitStatusChange(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("originalPath")] string? OriginalPath
);

internal sealed record GitStatusPage(
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("changes")] IReadOnlyList<GitStatusChange> Changes,
    [property: JsonPropertyName("nextOffset")] int? NextOffset
);

internal sealed class ReadOnlyGitRepository(string workspacePath)
{
    private const int MaximumOutputBytesPerStream = 128 * 1024;
    private const int MaximumCapturedOutputBytes = 16 * 1024 * 1024;
    private readonly string _workspacePath = Path.GetFullPath(workspacePath);

    internal async Task<GitStatusPage> StatusAsync(
        CancellationToken cancellationToken = default,
        [Description("Maximum change records, 1 to 500.")] int limit = 200,
        [Description(
            "Zero-based change offset; continue a previous page with the returned nextOffset."
        )]
            int offset = 0
    )
    {
        var page = new RecordPage(offset, limit);
        var output = await RunAsync(
            ["status", "--porcelain=v1", "-z", "--branch", "--untracked-files=all"],
            cancellationToken,
            maximumOutputBytes: MaximumCapturedOutputBytes
        );
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<GitStatusChange>();
        string? branch = null;
        var index = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.StartsWith("## ", StringComparison.Ordinal))
            {
                branch = field[3..];
                continue;
            }
            if (field.Length < 3)
            {
                throw new InvalidDataException("Invalid Git status record.");
            }
            var status = field[..2];
            var path = field[3..];
            string? originalPath = null;
            if (status.Contains('R') || status.Contains('C'))
            {
                if (++i >= fields.Length)
                {
                    throw new InvalidDataException("Incomplete Git rename record.");
                }
                originalPath = fields[i];
            }
            if (index++ < offset)
            {
                continue;
            }
            if (!page.TryAdd(path.Length + (originalPath?.Length ?? 0)))
            {
                return new GitStatusPage(branch, changes, page.NextOffset);
            }
            changes.Add(new GitStatusChange(status, path, originalPath));
        }
        if (offset > index)
        {
            throw page.OffsetBeyondEnd("change");
        }
        return new GitStatusPage(branch, changes, null);
    }

    internal async Task<TextPage> WorkspaceDiffAsync(
        [Description("Whether to inspect staged changes instead of unstaged changes.")]
            bool staged = false,
        [Description("Optional repository-relative path.")] string? path = null,
        [Description("Zero-based UTF-16 output offset.")] int offset = 0,
        [Description("Maximum UTF-16 code units to return, from 1 to 65536.")]
            int limit = BoundedTextPageReader.DefaultLimit,
        CancellationToken cancellationToken = default
    )
    {
        var arguments = new List<string> { "diff" };
        if (staged)
        {
            arguments.Add("--cached");
        }
        arguments.AddRange(["--no-ext-diff", "--no-textconv", "--no-color"]);
        AddPath(arguments, path);
        return await RunPagedAsync(arguments, offset, limit, cancellationToken);
    }

    internal async Task<string> LogAsync(
        [Description("Optional Git revision or range without whitespace or option prefixes.")]
            string? revision = null,
        [Description("Optional repository-relative path.")] string? path = null,
        [Description("Number of commits to skip, from 0 to 10000.")] int skip = 0,
        [Description("Maximum commits to return, from 1 to 100.")] int count = 20,
        CancellationToken cancellationToken = default
    )
    {
        if (skip is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(skip));
        }
        if (count is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        var arguments = new List<string>
        {
            "log",
            "--no-color",
            "--no-decorate",
            $"--skip={skip}",
            $"--max-count={count}",
            "--format=%H%x09%aI%x09%an%x09%s",
        };
        if (!string.IsNullOrWhiteSpace(revision))
        {
            arguments.Add(ValidateRevisionExpression(revision));
        }
        AddPath(arguments, path);
        var output = await RunAsync(arguments, cancellationToken);
        return output.Length == 0 ? "(no commits)" : output.TrimEnd('\n');
    }

    internal async Task<TextPage> ShowAsync(
        [Description("Exact commit SHA.")] string revision,
        [Description("Optional repository-relative path.")] string? path = null,
        [Description("Zero-based UTF-16 output offset.")] int offset = 0,
        [Description("Maximum UTF-16 code units to return, from 1 to 65536.")]
            int limit = BoundedTextPageReader.DefaultLimit,
        CancellationToken cancellationToken = default
    )
    {
        var arguments = new List<string>
        {
            "show",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            "--format=fuller",
            ValidateRevision(revision, nameof(revision)),
        };
        AddPath(arguments, path);
        return await RunPagedAsync(arguments, offset, limit, cancellationToken);
    }

    internal async Task<TextPage> BlameAsync(
        [Description("Repository-relative text file path.")] string path,
        [Description("Optional exact commit SHA.")] string? revision = null,
        [Description("Optional one-based first source line.")] int? startLine = null,
        [Description("Optional one-based last source line.")] int? endLine = null,
        [Description("Zero-based UTF-16 output offset.")] int offset = 0,
        [Description("Maximum UTF-16 code units to return, from 1 to 65536.")]
            int limit = BoundedTextPageReader.DefaultLimit,
        CancellationToken cancellationToken = default
    )
    {
        BoundedTextPageReader.ValidateBounds(offset, limit);
        var arguments = new List<string> { "blame", "--porcelain" };
        if (startLine is not null || endLine is not null)
        {
            if (startLine is null || endLine is null || startLine < 1 || endLine < startLine)
            {
                throw new ArgumentException("Blame line ranges require valid start and end lines.");
            }
            arguments.AddRange(["-L", $"{startLine},{endLine}"]);
        }
        if (!string.IsNullOrWhiteSpace(revision))
        {
            arguments.Add(ValidateRevision(revision, nameof(revision)));
        }
        arguments.Add("--");
        arguments.Add(ValidatePath(path));
        // git blame has no --output option, so its complete output is captured in memory.
        var output = await RunAsync(
            arguments,
            cancellationToken,
            maximumOutputBytes: MaximumCapturedOutputBytes
        );
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(output));
        return await BoundedTextPageReader.ReadAsync(stream, offset, limit, cancellationToken);
    }

    internal async Task<TextPage> ChangedFilesAsync(
        [Description("Exact base commit SHA.")] string baseSha,
        [Description("Exact candidate commit SHA.")] string candidateSha,
        [Description("Zero-based UTF-16 output offset.")] int offset = 0,
        [Description("Maximum UTF-16 code units to return, from 1 to 65536.")]
            int limit = BoundedTextPageReader.DefaultLimit,
        CancellationToken cancellationToken = default
    ) =>
        await RunPagedAsync(
            [
                "diff",
                "--name-status",
                "--find-renames",
                "--no-ext-diff",
                "--no-color",
                Range(baseSha, candidateSha),
            ],
            offset,
            limit,
            cancellationToken
        );

    internal async Task<TextPage> CompareAsync(
        [Description("Exact base commit SHA.")] string baseSha,
        [Description("Exact candidate commit SHA.")] string candidateSha,
        [Description("One path returned by git_changed_files, or omit for the complete diff.")]
            string? path = null,
        [Description("Zero-based UTF-16 output offset.")] int offset = 0,
        [Description("Maximum UTF-16 code units to return, from 1 to 65536.")]
            int limit = BoundedTextPageReader.DefaultLimit,
        CancellationToken cancellationToken = default
    )
    {
        var arguments = new List<string>
        {
            "diff",
            "--find-renames",
            "--no-ext-diff",
            "--no-textconv",
            "--no-color",
            Range(baseSha, candidateSha),
        };
        if (!string.IsNullOrWhiteSpace(path))
        {
            arguments.Add("--");
            arguments.Add(ValidatePath(path));
        }
        return await RunPagedAsync(arguments, offset, limit, cancellationToken);
    }

    private static string Range(string baseSha, string candidateSha) =>
        $"{ValidateRevision(baseSha, nameof(baseSha))}..{ValidateRevision(candidateSha, nameof(candidateSha))}";

    private static string ValidateRevision(string revision, string parameterName)
    {
        if (
            revision.Length is not (40 or 64)
            || revision.Any(character => !Uri.IsHexDigit(character))
        )
        {
            throw new ArgumentException(
                "A full hexadecimal commit SHA is required.",
                parameterName
            );
        }
        return revision;
    }

    private static string ValidateRevisionExpression(string revision)
    {
        if (
            revision.Length > 200
            || revision.StartsWith("-", StringComparison.Ordinal)
            || revision.Any(character =>
                char.IsWhiteSpace(character) || character is '\0' or '\r' or '\n'
            )
        )
        {
            throw new ArgumentException(
                "A bounded Git revision or range is required.",
                nameof(revision)
            );
        }
        return revision;
    }

    private void AddPath(List<string> arguments, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        arguments.Add("--");
        arguments.Add(ValidatePath(path));
    }

    private string ValidatePath(string path) =>
        Path.GetRelativePath(
                _workspacePath,
                WorkspacePathAuthority.Resolve(_workspacePath, path, "Git inspection")
            )
            .Replace('\\', '/');

    private async Task<TextPage> RunPagedAsync(
        IReadOnlyList<string> arguments,
        int offset,
        int limit,
        CancellationToken cancellationToken
    )
    {
        BoundedTextPageReader.ValidateBounds(offset, limit);
        var outputPath = Path.GetTempFileName();
        try
        {
            await RunAsync(arguments, cancellationToken, outputPath);
            return await BoundedTextPageReader.ReadAsync(
                outputPath,
                offset,
                limit,
                cancellationToken
            );
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    // With an output path, git writes the complete diff there (before any "--" pathspec) instead of stdout.
    private async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? outputPath = null,
        int maximumOutputBytes = MaximumOutputBytesPerStream
    )
    {
        var separator = arguments.TakeWhile(argument => argument != "--").Count();
        IReadOnlyList<string> command = outputPath is null
            ? arguments
            :
            [
                .. arguments.Take(separator),
                $"--output={outputPath}",
                .. arguments.Skip(separator),
            ];
        var result = await GitProcess.RunAsync(
            _workspacePath,
            command,
            maximumOutputBytes,
            cancellationToken
        );
        if (result.TimedOut)
        {
            throw new TimeoutException();
        }
        if (result.StdoutTruncated || result.StderrTruncated)
        {
            throw new InvalidOperationException(
                "Read-only Git inspection exceeded the complete-output capture limit."
            );
        }
        if (result.ExitCode == 0)
        {
            return result.Stdout;
        }
        if (
            result.Stderr.Contains("bad object", StringComparison.OrdinalIgnoreCase)
            || result.Stderr.Contains("bad revision", StringComparison.OrdinalIgnoreCase)
            || result.Stderr.Contains("unknown revision", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ToolInputException(
                $"Unknown Git revision. Use git_log to obtain a valid revision. {result.Stderr.Trim()}"
            );
        }
        throw new InvalidOperationException(
            $"Read-only Git inspection failed: {result.Stderr.Trim()}"
        );
    }
}
