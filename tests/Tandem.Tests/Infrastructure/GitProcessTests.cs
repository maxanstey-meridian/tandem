using System.Diagnostics;
using FluentAssertions;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class GitProcessTests
{
    private static readonly string _gitPath =
        Environment.GetEnvironmentVariable("TANDEM_TEST_GIT") ?? "git";

    [Fact]
    public async Task RunAsync_ReturnsStdoutTrimmedByCallerAndExitCode()
    {
        var git = new GitProcess(_gitPath);
        var result = await git.RunAsync(null, ["--version"], CancellationToken.None);
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("git version");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_PassesArgumentsSeparatelyNotAsShellString()
    {
        // An argument with spaces and quotes must arrive as a single argument, proving
        // per-arg invocation rather than shell-string construction.
        using var temp = TempDir.Create();
        RunGitInit(temp.Dir);
        RunGit(temp.Dir, ["config", "user.email", "t@t.test"]);
        RunGit(temp.Dir, ["config", "user.name", "Tandem Test"]);
        File.WriteAllText(Path.Combine(temp.Dir, "f.txt"), "x\n");
        RunGit(temp.Dir, ["add", "-A"]);
        RunGit(temp.Dir, ["commit", "-qm", "message with spaces and 'quotes'"]);

        var git = new GitProcess(_gitPath);
        var result = await git.RunAsync(
            temp.Dir,
            ["log", "-1", "--pretty=%s"],
            CancellationToken.None
        );
        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().Be("message with spaces and 'quotes'");
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellationAndReturnsResult()
    {
        // Use a fake git that sleeps. This exercises GitProcess's kill-on-cancel
        // path without depending on a real git command that blocks indefinitely.
        using var temp = TempDir.Create();
        var fakeGit = Path.Combine(temp.Dir, "fake-git.sh");
        await File.WriteAllTextAsync(fakeGit, "#!/bin/sh\nsleep 30\n", CancellationToken.None);
        var chmod =
            Process.Start(
                new ProcessStartInfo("chmod", ["+x", fakeGit])
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            ) ?? throw new InvalidOperationException("chmod failed to start");
        chmod.WaitForExit();

        var git = new GitProcess(fakeGit);
        using var cts = new CancellationTokenSource();

        var runTask = git.RunAsync(temp.Dir, ["--version"], cts.Token);
        await Task.Delay(300, CancellationToken.None);
        cts.Cancel();

        var result = await runTask;

        result.TimedOut.Should().BeFalse();
        result.ExitCode.Should().NotBe(0);
    }

    private static void RunGitInit(string dir)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _gitPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            },
        };
        p.StartInfo.ArgumentList.Add("init");
        p.StartInfo.ArgumentList.Add("-q");
        p.Start();
        p.WaitForExit();
    }

    private static void RunGit(string dir, string[] args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _gitPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            },
        };
        foreach (var a in args)
        {
            p.StartInfo.ArgumentList.Add(a);
        }

        p.Start();
        p.WaitForExit();
    }

    private sealed class TempDir : IDisposable
    {
        public string Dir { get; }

        private TempDir(string dir)
        {
            Dir = dir;
        }

        public static TempDir Create()
        {
            var dir = Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-git-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(dir);
            return new TempDir(dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch { }
        }
    }
}
