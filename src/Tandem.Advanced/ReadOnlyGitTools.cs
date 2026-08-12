using System.ComponentModel;
using System.Text;
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
                "Inspect the current branch and every staged, unstaged, and untracked workspace change."
            ),
            AIFunctionFactory.Create(
                repository.WorkspaceDiffAsync,
                DiffToolName,
                "Read a paginated staged or unstaged workspace diff, optionally restricted to one repository-relative path."
            ),
            AIFunctionFactory.Create(
                repository.LogAsync,
                LogToolName,
                "Read bounded Git history with stable machine-readable commit formatting."
            ),
            AIFunctionFactory.Create(
                repository.ShowAsync,
                ShowToolName,
                "Read a paginated commit and patch for one exact Git revision, optionally restricted to one path."
            ),
            AIFunctionFactory.Create(
                repository.BlameAsync,
                BlameToolName,
                "Read bounded line attribution for one repository-relative text file."
            ),
            AIFunctionFactory.Create(
                repository.ChangedFilesAsync,
                ChangedFilesToolName,
                "List every path changed between two exact Git revisions, including additions, deletions, and renames. Results are paginated."
            ),
            AIFunctionFactory.Create(
                repository.CompareAsync,
                CompareToolName,
                "Read a paginated exact textual Git diff between two exact revisions, optionally restricted to one changed path. Call git_changed_files first, then inspect every returned path."
            ),
        };
        var existing = options.Tools ?? [];
        foreach (var tool in tools)
        {
            if (existing.Any(candidate => candidate.Name == tool.Name))
            {
                throw new InvalidOperationException($"Agent already exposes tool '{tool.Name}'.");
            }
            toolEffects.Add(
                tool.Name,
                Infrastructure.ToolEffect.Read,
                Infrastructure.ToolEvidence.RepositoryInspection
            );
        }
        options.Tools = [.. existing, .. tools];
    }
}

internal sealed class ReadOnlyGitRepository(string workspacePath)
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private const int MaximumOutputBytesPerStream = 16 * 1024 * 1024;
    private readonly string _workspacePath = Path.GetFullPath(workspacePath);

    internal async Task<string> StatusAsync(CancellationToken cancellationToken = default) =>
        Page(
            await RunAsync(
                ["status", "--porcelain=v1", "--branch", "--untracked-files=all"],
                cancellationToken
            ),
            1,
            500,
            "(clean workspace)"
        );

    internal async Task<string> WorkspaceDiffAsync(
        [Description("Whether to inspect staged changes instead of unstaged changes.")]
            bool staged = false,
        [Description("Optional repository-relative path.")] string? path = null,
        [Description("One-based diff line to start from.")] int startLine = 1,
        [Description("Maximum lines to return, from 1 to 500.")] int maxLines = 300,
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
        return Page(await RunAsync(arguments, cancellationToken), startLine, maxLines);
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
        return Page(await RunAsync(arguments, cancellationToken), 1, 500, "(no commits)");
    }

    internal async Task<string> ShowAsync(
        [Description("Exact commit SHA.")] string revision,
        [Description("Optional repository-relative path.")] string? path = null,
        [Description("One-based output line to start from.")] int startLine = 1,
        [Description("Maximum lines to return, from 1 to 500.")] int maxLines = 300,
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
        return Page(await RunAsync(arguments, cancellationToken), startLine, maxLines);
    }

    internal async Task<string> BlameAsync(
        [Description("Repository-relative text file path.")] string path,
        [Description("Optional exact commit SHA.")] string? revision = null,
        [Description("Optional one-based first source line.")] int? startLine = null,
        [Description("Optional one-based last source line.")] int? endLine = null,
        CancellationToken cancellationToken = default
    )
    {
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
        return Page(await RunAsync(arguments, cancellationToken), 1, 500);
    }

    internal async Task<string> ChangedFilesAsync(
        [Description("Exact base commit SHA.")] string baseSha,
        [Description("Exact candidate commit SHA.")] string candidateSha,
        [Description("One-based output line to start from.")] int startLine = 1,
        [Description("Maximum lines to return, from 1 to 500.")] int maxLines = 200,
        CancellationToken cancellationToken = default
    ) =>
        Page(
            await RunAsync(
                [
                    "diff",
                    "--name-status",
                    "--find-renames",
                    "--no-ext-diff",
                    "--no-color",
                    Range(baseSha, candidateSha),
                ],
                cancellationToken
            ),
            startLine,
            maxLines
        );

    internal async Task<string> CompareAsync(
        [Description("Exact base commit SHA.")] string baseSha,
        [Description("Exact candidate commit SHA.")] string candidateSha,
        [Description("One path returned by git_changed_files, or omit for the complete diff.")]
            string? path = null,
        [Description("One-based diff line to start from.")] int startLine = 1,
        [Description("Maximum lines to return, from 1 to 500.")] int maxLines = 300,
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
        return Page(await RunAsync(arguments, cancellationToken), startLine, maxLines);
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

    private string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.IndexOfAny(['\0', '\r', '\n']) >= 0 || Path.IsPathRooted(path))
        {
            throw new ArgumentException("A relative repository path is required.", nameof(path));
        }
        var fullPath = Path.GetFullPath(Path.Combine(_workspacePath, path));
        var relative = Path.GetRelativePath(_workspacePath, fullPath);
        if (
            relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == ".git")
        )
        {
            throw new ArgumentException("Path must remain inside the repository.", nameof(path));
        }
        return relative.Replace('\\', '/');
    }

    private async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        var result = await LocalProcess.RunAsync(
            new LocalProcessRequest(
                "git",
                ["-c", "core.fsmonitor=false", .. arguments],
                _workspacePath,
                _timeout,
                MaximumOutputBytesPerStream,
                new Dictionary<string, string>
                {
                    ["GIT_PAGER"] = "cat",
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GIT_OPTIONAL_LOCKS"] = "0",
                }
            ),
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
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Read-only Git inspection failed: {result.Stderr.Trim()}"
            );
        }
        return result.Stdout;
    }

    private static string Page(
        string output,
        int startLine,
        int maxLines,
        string emptyResult = "(no changes)"
    )
    {
        if (startLine < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(startLine));
        }
        if (maxLines is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines));
        }
        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n')
            .Split('\n');
        if (lines.Length == 1 && lines[0].Length == 0)
        {
            return emptyResult;
        }
        if (startLine > lines.Length)
        {
            return $"[no lines at {startLine}; total lines: {lines.Length}]";
        }
        var page = lines.Skip(startLine - 1).Take(maxLines).ToArray();
        var endLine = startLine + page.Length - 1;
        var marker =
            endLine < lines.Length
                ? $"[lines {startLine}-{endLine} of {lines.Length}; continue at startLine {endLine + 1}]"
                : $"[lines {startLine}-{endLine} of {lines.Length}; complete]";
        var text = new StringBuilder(marker).AppendLine();
        foreach (var line in page)
        {
            text.AppendLine(line);
        }
        return text.ToString().TrimEnd();
    }
}
