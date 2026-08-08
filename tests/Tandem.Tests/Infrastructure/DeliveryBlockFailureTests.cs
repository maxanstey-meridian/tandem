using System.Diagnostics;
using FluentAssertions;
using Tandem.Domain;

namespace Tandem.Tests.Infrastructure;

public sealed class DeliveryBlockFailureTests
{
    [Fact]
    public async Task CaptureCandidate_DoesNotEmitSuccessWhenGitAddFails()
    {
        using var temp = TempDir.Create();
        var fakeGit = await CreateExecutableAsync(
            temp.Path,
            "fake-git.sh",
            "#!/bin/sh\necho add rejected >&2\nexit 23\n"
        );
        var stage = new CaptureCandidateStage(new GitProcess(fakeGit));
        var message = CreateMessage(temp.Path, []);

        var act = () => stage.ExecuteAsync(message.State, CancellationToken.None).AsTask();

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            "*git add*exit code 23*add rejected*"
        );
    }

    [Fact]
    public async Task Verification_RecordsActualIndexAndInternalTimeoutEvidence()
    {
        using var temp = TempDir.Create();
        var block = new VerificationBlock(
            new GitProcess(),
            commandTimeout: TimeSpan.FromMilliseconds(100)
        );
        var message = CreateMessage(temp.Path, ["true", "true", "true", "true", "sleep 30"]);
        message = message with { State = message.State with { VerificationIndex = 4 } };

        var result = await block.ExecuteAsync(Context(message), CancellationToken.None);

        result.State.VerificationResults.Should().ContainSingle();
        var command = result.State.VerificationResults.Single();
        command.Index.Should().Be(4);
        command.TimedOut.Should().BeTrue();
        command.ExitCode.Should().Be(-1);
        command.Stderr.Should().Contain("timed out after");
        result.Outcome.Kind.Should().Be(OutcomeKinds.CommandFailed);
    }

    [Fact]
    public async Task Verification_PropagatesCallerCancellation()
    {
        using var temp = TempDir.Create();
        var block = new VerificationBlock(
            new GitProcess(),
            commandTimeout: TimeSpan.FromMinutes(1)
        );
        var message = CreateMessage(temp.Path, ["sleep 30"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => block.ExecuteAsync(Context(message), cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Verification_RejectsChangesOutsideCapturedCandidate()
    {
        using var temp = TempDir.Create();
        var git = new GitProcess();
        await git.RunAsync(temp.Path, ["init"], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "tracked.txt"), "before");
        await git.RunAsync(temp.Path, ["add", "-A"], CancellationToken.None);
        await git.RunAsync(
            temp.Path,
            ["-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", "base"],
            CancellationToken.None
        );
        var head = await git.RunAsync(temp.Path, ["rev-parse", "HEAD"], CancellationToken.None);
        var message = CreateMessage(temp.Path, ["printf changed > tracked.txt"]);
        message = message with { State = message.State with { CandidateSha = head.Stdout.Trim() } };

        var result = await new VerificationBlock(new GitProcess()).ExecuteAsync(
            Context(message),
            CancellationToken.None
        );

        result.Outcome.Kind.Should().Be(OutcomeKinds.CommandFailed);
        result.State.VerificationResults.Single().Stderr.Should().Contain("must be read-only");
    }

    private static PipelineMessage<DeliveryState> CreateMessage(
        string workspace,
        IReadOnlyList<string> verification
    )
    {
        var packet = new Packet("test", workspace, "main", [], verification, [], "");
        return new PipelineMessage<DeliveryState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            DeliveryState.Create(packet, "base", workspace)
        );
    }

    private static PipelineOperationContext<DeliveryState> Context(
        PipelineMessage<DeliveryState> message
    ) => new(message);

    private static async Task<string> CreateExecutableAsync(
        string directory,
        string name,
        string contents
    )
    {
        var path = System.IO.Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, contents);
        using var chmod = Process.Start("chmod", ["+x", path])!;
        await chmod.WaitForExitAsync();
        return path;
    }

    private sealed class TempDir : IDisposable
    {
        private TempDir(string path) => Path = path;

        public string Path { get; }

        public static TempDir Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-block-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(path);
            return new TempDir(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
