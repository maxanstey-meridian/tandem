using System.Diagnostics;
using FluentAssertions;

namespace Tandem.Tests.Infrastructure;

public sealed class ReadOnlyGitToolsTests
{
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
        var firstPage = await git.CompareAsync(baseSha, candidateSha, "feature.txt", maxLines: 1);
        var deleted = await git.CompareAsync(baseSha, candidateSha, "deleted.txt");
        var empty = await git.CompareAsync(candidateSha, candidateSha);

        changed.Should().Contain("feature.txt").And.Contain("deleted.txt");
        firstPage.Should().Contain("continue at startLine 2");
        deleted.Should().Contain("-deleted").And.Contain("complete");
        empty.Should().Be("(no changes)");
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
        staged.Should().Contain("+staged");
        unstaged.Should().Contain("+unstaged");
        log.Should().Contain(sha).And.Contain("base");
        show.Should().Contain("commit " + sha).And.Contain("+base");
        blame.Should().Contain(sha).And.Contain("base");
    }

    [Fact]
    public async Task Git_capture_fails_closed_before_pagination()
    {
        using var repository = TestRepository.Create();
        File.WriteAllText(Path.Combine(repository.Path, "large.txt"), "base\n");
        repository.Commit("base");
        var baseSha = repository.Head();
        File.WriteAllText(Path.Combine(repository.Path, "large.txt"), new string('x', 17_000_000));
        repository.Commit("large");

        var inspect = async () =>
            await new ReadOnlyGitRepository(repository.Path).CompareAsync(
                baseSha,
                repository.Head(),
                "large.txt"
            );

        await inspect
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*complete-output capture limit*");
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

        output.Should().Contain("-before").And.Contain("+after");
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
