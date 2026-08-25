using System.Diagnostics;
using FluentAssertions;

namespace Tandem.Tests.Infrastructure;

public sealed class ReadOnlyGitToolsTests
{
    [Fact]
    public void Compare_description_does_not_make_diff_traversal_a_review_proxy()
    {
        ReadOnlyGitTools.CompareDescription.Should().Contain("when the diff is relevant");
        ReadOnlyGitTools
            .CompareDescription.Should()
            .Contain("path filtering and pagination as needed");
        ReadOnlyGitTools
            .CompareDescription.Should()
            .Contain("do not treat a sampled diff as complete evidence");
        ReadOnlyGitTools.CompareDescription.Should().NotContain("inspect every returned path");
    }

    [Fact]
    public async Task Changed_files_and_diff_are_complete_and_paginated()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "feature.txt"), "before\n");
        File.WriteAllText(Path.Combine(repository.Path, "deleted.txt"), "deleted\n");
        repository.Commit("base");
        var baseSha = repository.Head();

        File.WriteAllText(Path.Combine(repository.Path, "feature.txt"), "after\nsecond\n");
        File.Delete(Path.Combine(repository.Path, "deleted.txt"));
        repository.Commit("candidate");
        var candidateSha = repository.Head();
        var git = new ReadOnlyGitRepository(repository.Path);

        var changed = await git.ChangedFilesAsync(baseSha, candidateSha);
        var firstPage = await git.CompareAsync(baseSha, candidateSha, "feature.txt", limit: 64);
        var deleted = await git.CompareAsync(baseSha, candidateSha, "deleted.txt");
        var empty = await git.CompareAsync(candidateSha, candidateSha);

        changed.Content.Should().Contain("feature.txt").And.Contain("deleted.txt");
        firstPage.HasMore.Should().BeTrue();
        deleted.Content.Should().Contain("-deleted");
        deleted.HasMore.Should().BeFalse();
        empty.Should().BeEquivalentTo(new TextPage("", 0, 0, 0, false, null));
    }

    [Fact]
    public async Task Workspace_inspection_covers_status_staged_diff_log_show_and_blame()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "tracked.txt"), "base\n");
        repository.Commit("base");
        var sha = repository.Head();
        File.WriteAllText(Path.Combine(repository.Path, "tracked.txt"), "staged\n");
        repository.RunPublic("add", "tracked.txt");
        File.AppendAllText(Path.Combine(repository.Path, "tracked.txt"), "unstaged\n");
        File.WriteAllText(Path.Combine(repository.Path, "untracked.txt"), "new\n");
        var git = new ReadOnlyGitRepository(repository.Path);

        var status = await git.StatusAsync();
        var staged = await git.WorkspaceDiffAsync(staged: true);
        var unstaged = await git.WorkspaceDiffAsync();
        var log = await git.LogAsync(count: 1);
        var show = await git.ShowAsync(sha, "tracked.txt");
        var blame = await git.BlameAsync("tracked.txt", sha, 1, 1);

        status.Should().Contain("tracked.txt").And.Contain("untracked.txt");
        staged.Content.Should().Contain("+staged");
        unstaged.Content.Should().Contain("+unstaged");
        log.Should().Contain(sha).And.Contain("base");
        show.Content.Should().Contain("commit " + sha).And.Contain("+base");
        blame.Should().Contain(sha).And.Contain("base");
    }

    [Fact]
    public async Task Large_diffs_page_from_complete_on_disk_capture()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "large.txt"), "base\n");
        repository.Commit("base");
        var baseSha = repository.Head();
        File.WriteAllText(Path.Combine(repository.Path, "large.txt"), new string('x', 200_000));
        repository.Commit("large");
        var candidateSha = repository.Head();
        var git = new ReadOnlyGitRepository(repository.Path);

        var firstPage = await git.CompareAsync(baseSha, candidateSha, "large.txt", limit: 64);

        firstPage.Length.Should().BeLessThanOrEqualTo(64);
        firstPage.HasMore.Should().BeTrue();

        var fullPage = await git.CompareAsync(baseSha, candidateSha, "large.txt", limit: 65_536);
        fullPage.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Repository_wide_diff_pages_when_total_exceeds_capture_limit()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "a.txt"), "base\n");
        File.WriteAllText(Path.Combine(repository.Path, "b.txt"), "base\n");
        repository.Commit("base");
        var baseSha = repository.Head();
        File.WriteAllText(Path.Combine(repository.Path, "a.txt"), new string('x', 200_000));
        File.WriteAllText(Path.Combine(repository.Path, "b.txt"), new string('y', 200_000));
        repository.Commit("large");
        var candidateSha = repository.Head();
        var git = new ReadOnlyGitRepository(repository.Path);

        var firstPage = await git.CompareAsync(baseSha, candidateSha, limit: 64);

        firstPage.Length.Should().BeLessThanOrEqualTo(64);
        firstPage.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Every_paginated_command_reconstructs_output_beyond_process_capture_limit()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "large.txt"), "base\n");
        repository.Commit("base");
        var baseSha = repository.Head();
        File.WriteAllText(Path.Combine(repository.Path, "large.txt"), new string('x', 200_000));
        for (var index = 0; index < 2_500; index++)
        {
            File.WriteAllText(
                Path.Combine(repository.Path, $"changed-{index:D4}-{new string('n', 45)}.txt"),
                "x"
            );
        }
        repository.Commit("candidate");
        var candidateSha = repository.Head();
        var git = new ReadOnlyGitRepository(repository.Path);

        var compare = await Reconstruct(offset =>
            git.CompareAsync(baseSha, candidateSha, limit: 32_768, offset: offset)
        );
        var changed = await Reconstruct(offset =>
            git.ChangedFilesAsync(baseSha, candidateSha, offset, 32_768)
        );
        var show = await Reconstruct(offset =>
            git.ShowAsync(candidateSha, offset: offset, limit: 32_768)
        );

        compare
            .Should()
            .Be(
                repository.RunPublic(
                    "diff",
                    "--find-renames",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--no-color",
                    $"{baseSha}..{candidateSha}"
                )
            );
        changed
            .Should()
            .Be(
                repository.RunPublic(
                    "diff",
                    "--name-status",
                    "--find-renames",
                    "--no-ext-diff",
                    "--no-color",
                    $"{baseSha}..{candidateSha}"
                )
            );
        show.Should()
            .Be(
                repository.RunPublic(
                    "show",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--no-color",
                    "--format=fuller",
                    candidateSha
                )
            );
        compare.Length.Should().BeGreaterThan(128 * 1024);
        changed.Length.Should().BeGreaterThan(128 * 1024);
        show.Length.Should().BeGreaterThan(128 * 1024);

        File.WriteAllText(Path.Combine(repository.Path, "large.txt"), new string('z', 210_000));
        var workspace = await Reconstruct(offset =>
            git.WorkspaceDiffAsync(offset: offset, limit: 32_768)
        );
        workspace
            .Should()
            .Be(repository.RunPublic("diff", "--no-ext-diff", "--no-textconv", "--no-color"));
        workspace.Length.Should().BeGreaterThan(128 * 1024);
    }

    [Fact]
    public async Task Paged_git_capture_is_deleted_after_success_failure_and_cancellation()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "tracked.txt"), "base\n");
        repository.Commit("base");
        File.WriteAllText(Path.Combine(repository.Path, "tracked.txt"), "changed\n");
        var captures = new List<string>();
        string CreateCapture()
        {
            var path = Path.Combine(repository.Path, $"capture-{captures.Count}.tmp");
            using (File.Create(path)) { }
            captures.Add(path);
            return path;
        }
        var git = new ReadOnlyGitRepository(repository.Path, CreateCapture);

        await git.WorkspaceDiffAsync();
        File.Exists(captures[^1]).Should().BeFalse();

        var missingRepository = Path.Combine(repository.Path, "missing");
        var failing = new ReadOnlyGitRepository(missingRepository, CreateCapture);
        await FluentActions
            .Awaiting(() => failing.WorkspaceDiffAsync())
            .Should()
            .ThrowAsync<Exception>();
        File.Exists(captures[^1]).Should().BeFalse();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await FluentActions
            .Awaiting(() => git.WorkspaceDiffAsync(cancellationToken: cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        File.Exists(captures[^1]).Should().BeFalse();
    }

    private static async Task<string> Reconstruct(Func<int, Task<TextPage>> read)
    {
        var content = new System.Text.StringBuilder();
        var offset = 0;
        while (true)
        {
            var page = await read(offset);
            page.Offset.Should().Be(offset);
            page.Length.Should().Be(page.Content.Length).And.BeLessThanOrEqualTo(32_768);
            content.Append(page.Content);
            if (!page.HasMore)
            {
                page.NextOffset.Should().BeNull();
                return content.ToString();
            }
            page.NextOffset.Should().Be(offset + page.Length);
            page.NextOffset.Should().BeGreaterThan(offset);
            offset = page.NextOffset!.Value;
        }
    }

    [Fact]
    public async Task Diff_disables_external_drivers_and_text_conversion()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, ".gitattributes"), "*.txt diff=hostile\n");
        File.WriteAllText(Path.Combine(repository.Path, "tracked.txt"), "before\n");
        repository.RunPublic("config", "diff.external", "false");
        repository.RunPublic("config", "diff.hostile.textconv", "false");
        repository.Commit("base");
        var baseSha = repository.Head();
        File.WriteAllText(Path.Combine(repository.Path, "tracked.txt"), "after\n");
        repository.Commit("candidate");

        var output = await new ReadOnlyGitRepository(repository.Path).CompareAsync(
            baseSha,
            repository.Head(),
            "tracked.txt"
        );

        output.Content.Should().Contain("-before").And.Contain("+after");
    }

    [Fact]
    public async Task Status_disables_repository_configured_file_system_monitor()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "tracked.txt"), "base\n");
        repository.Commit("base");
        var marker = Path.Combine(repository.Path, "fsmonitor-invoked");
        var hook = Path.Combine(
            repository.Path,
            OperatingSystem.IsWindows() ? "fsmonitor.cmd" : "fsmonitor.sh"
        );
        File.WriteAllText(
            hook,
            OperatingSystem.IsWindows()
                ? $"@echo off\r\ntype nul > \"{marker}\"\r\necho 0\r\n"
                : $"#!/bin/sh\ntouch '{marker}'\nprintf '0\\n'\n"
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                hook,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        repository.RunPublic("config", "core.fsmonitor", hook);

        await new ReadOnlyGitRepository(repository.Path).StatusAsync();

        File.Exists(marker).Should().BeFalse();
    }

    [Fact]
    public async Task Git_operations_honor_caller_cancellation()
    {
        using var repository = TestRepository.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var run = async () =>
            await new ReadOnlyGitRepository(repository.Path).StatusAsync(cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("main")]
    [InlineData("abc")]
    [InlineData("--help")]
    public async Task Exact_revision_tools_reject_non_full_shas(string revision)
    {
        using var repository = TestRepository.Create();
        var git = new ReadOnlyGitRepository(repository.Path);

        var show = async () => await git.ShowAsync(revision);
        var compare = async () => await git.CompareAsync(revision, new string('a', 40));

        await show.Should().ThrowAsync<ArgumentException>();
        await compare.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".git/config")]
    [InlineData("/absolute.txt")]
    public async Task Diff_rejects_paths_outside_the_read_only_repository(string path)
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "feature.txt"), "content\n");
        repository.Commit("base");
        var sha = repository.Head();

        var act = async () =>
            await new ReadOnlyGitRepository(repository.Path).CompareAsync(sha, sha, path);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class TestRepository : IDisposable
    {
        private TestRepository(string path)
        {
            Path = path;
        }

        internal string Path { get; }

        internal static TestRepository Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tandem-readonly-git-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(path);
            var repository = new TestRepository(path);
            repository.Run("init", "-q");
            repository.Run("config", "user.name", "Tandem Tests");
            repository.Run("config", "user.email", "tandem-tests@localhost");
            return repository;
        }

        internal void Commit(string message)
        {
            Run("add", "-A");
            Run("commit", "-qm", message);
        }

        internal string Head() => Run("rev-parse", "HEAD").Trim();

        internal string RunPublic(params string[] arguments) => Run(arguments);

        private string Run(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var process =
                Process.Start(startInfo)
                ?? throw new InvalidOperationException("Git failed to start.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(stderr);
            }
            return stdout;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
