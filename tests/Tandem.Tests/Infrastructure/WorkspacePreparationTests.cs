using System.Diagnostics;
using FluentAssertions;
using Tandem.Domain;

namespace Tandem.Tests.Infrastructure;

public sealed class WorkspacePreparationTests
{
    private static readonly string _gitPath =
        Environment.GetEnvironmentVariable("TANDEM_TEST_GIT") ?? "git";

    [Fact]
    public async Task PrepareAsync_ClonesPinsAndDetachesAtBaseSha()
    {
        using var source = TempSourceRepo.Create();
        using var run = TempRunDir.Create();
        var packet = MakePacket(source.Path, "main");
        var prep = new WorkspacePreparation(new GitProcess(_gitPath));

        var result = await prep.PrepareAsync(
            packet,
            run.Dir,
            Path.Combine(run.Dir, "workspace"),
            CancellationToken.None
        );

        result.PinnedBaseSha.Should().Be(source.HeadSha);
        result.WorkspacePath.Should().Be(Path.Combine(run.Dir, "workspace"));
        Directory.Exists(result.WorkspacePath).Should().BeTrue();
        await AssertHeadEquals(result.WorkspacePath, source.HeadSha);
        await AssertNoRemotes(result.WorkspacePath);
    }

    [Fact]
    public async Task PrepareAsync_WorkspacePathBelongsToRunId()
    {
        using var source = TempSourceRepo.Create();
        using var run = TempRunDir.Create();
        var packet = MakePacket(source.Path, "main");
        var prep = new WorkspacePreparation(new GitProcess(_gitPath));

        var result = await prep.PrepareAsync(
            packet,
            run.Dir,
            Path.Combine(run.Dir, "workspace"),
            CancellationToken.None
        );

        result.WorkspacePath.Should().StartWith(run.Dir);
        Path.GetFileName(result.WorkspacePath).Should().Be("workspace");
    }

    [Fact]
    public async Task PrepareAsync_EditingWorkspaceDoesNotTouchSource()
    {
        using var source = TempSourceRepo.Create();
        File.WriteAllText(Path.Combine(source.Path, "readme.md"), "original\n");
        source.Commit("add readme");
        using var run = TempRunDir.Create();
        var workspacePath = Path.Combine(run.Dir, "workspace");
        var packet = MakePacket(source.Path, "main");
        var prep = new WorkspacePreparation(new GitProcess(_gitPath));

        await prep.PrepareAsync(packet, run.Dir, workspacePath, CancellationToken.None);

        File.Exists(Path.Combine(workspacePath, "readme.md")).Should().BeTrue();
        File.WriteAllText(Path.Combine(workspacePath, "readme.md"), "edited by agent\n");

        File.ReadAllText(Path.Combine(source.Path, "readme.md"))
            .Should()
            .Be("original\n", "the source repository must remain unchanged");
        await AssertWorkingTreeClean(source.Path);
    }

    [Fact]
    public async Task PrepareAsync_BadBaseRefFailsAndDeletesRunDirectory()
    {
        using var source = TempSourceRepo.Create();
        using var run = TempRunDir.Create();
        var packet = MakePacket(source.Path, "does-not-exist");
        var prep = new WorkspacePreparation(new GitProcess(_gitPath));
        var workspacePath = Path.Combine(run.Dir, "workspace");

        var act = async () =>
            await prep.PrepareAsync(packet, run.Dir, workspacePath, CancellationToken.None);

        (await act.Should().ThrowAsync<WorkspacePreparationException>()).WithMessage(
            "*does-not-exist*"
        );
        Directory.Exists(run.Dir).Should().BeFalse("the run directory must be deleted on failure");
    }

    private static Packet MakePacket(string repository, string @base) =>
        new(
            Title: "test",
            Repository: repository,
            Base: @base,
            Outcomes: [new Outcome("greeting", "Create greeting.txt")],
            Verification: [],
            Constraints: [],
            ImplementationContext: ""
        );

    private static async Task AssertHeadEquals(string workspace, string expectedSha)
    {
        var result = await new GitProcess(_gitPath).RunAsync(
            null,
            ["-C", workspace, "rev-parse", "HEAD"],
            CancellationToken.None
        );
        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().Be(expectedSha);
    }

    private static async Task AssertNoRemotes(string workspace)
    {
        var result = await new GitProcess(_gitPath).RunAsync(
            null,
            ["-C", workspace, "remote"],
            CancellationToken.None
        );
        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().BeEmpty("the workspace must have no remotes");
    }

    private static async Task AssertWorkingTreeClean(string repo)
    {
        var result = await new GitProcess(_gitPath).RunAsync(
            repo,
            ["status", "--porcelain"],
            CancellationToken.None
        );
        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().BeEmpty("the source repository working tree must be clean");
    }

    private sealed class TempSourceRepo : IDisposable
    {
        public string Path { get; }
        public string HeadSha { get; }

        private TempSourceRepo(string path, string headSha)
        {
            Path = path;
            HeadSha = headSha;
        }

        public static TempSourceRepo Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-src-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(path);
            RunGit(path, ["init", "-q"]);
            RunGit(path, ["config", "user.email", "t@t.test"]);
            RunGit(path, ["config", "user.name", "Tandem Test"]);
            File.WriteAllText(System.IO.Path.Combine(path, "anchor.txt"), "anchor\n");
            RunGit(path, ["add", "-A"]);
            RunGit(path, ["commit", "-qm", "init"]);
            RunGit(path, ["branch", "-m", "main"]);
            var shaResult = RunGit(path, ["rev-parse", "HEAD"]);
            return new TempSourceRepo(path, shaResult.Stdout.Trim());
        }

        public void Commit(string message)
        {
            RunGit(Path, ["add", "-A"]);
            RunGit(Path, ["commit", "-qm", message]);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch { }
        }

        private static GitResult RunGit(string workingDir, string[] args)
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
                    WorkingDirectory = workingDir,
                },
            };
            foreach (var a in args)
            {
                p.StartInfo.ArgumentList.Add(a);
            }

            p.Start();
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return new GitResult(p.ExitCode, stdoutTask.Result, stderrTask.Result, false);
        }
    }

    private sealed class TempRunDir : IDisposable
    {
        public string Dir { get; }

        private TempRunDir(string dir)
        {
            Dir = dir;
        }

        public static TempRunDir Create()
        {
            var dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-run-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(dir);
            return new TempRunDir(dir);
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
