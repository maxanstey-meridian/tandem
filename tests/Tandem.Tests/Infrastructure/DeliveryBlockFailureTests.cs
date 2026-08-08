using System.Diagnostics;
using FluentAssertions;
using Tandem.Domain;
using Tandem.Ledger;
using Tandem.Tool;

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
        var stage = new CaptureCandidateStage(
            new GitProcess(fakeGit),
            new FakeDeliveryRecordSink()
        );
        var message = CreateMessage(temp.Path, []);

        var act = () => stage.ExecuteAsync(message.State, CancellationToken.None).AsTask();

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
            "*git add*exit code 23*add rejected*"
        );
    }

    [Fact]
    public async Task CheckpointAcceptance_DerivesChangedFilesOutcomesAndConstraints()
    {
        using var temp = TempDir.Create();
        var fakeGit = await CreateExecutableAsync(
            temp.Path,
            "checkpoint-git.sh",
            "#!/bin/sh\nif [ \"$1\" = \"diff\" ]; then printf 'src/B.cs\\nsrc/A.cs\\n'; else printf 'new.txt\\n'; fi\n"
        );
        var records = new FakeDeliveryRecordSink();
        var packet = new Packet(
            "test",
            temp.Path,
            "main",
            [new Outcome("outcome", "Deliver it")],
            [],
            ["packet constraint"],
            ""
        );
        var state = DeliveryState.Create(packet, "base", temp.Path) with
        {
            PlannerConstraints = ["planner constraint"],
        };
        var acceptance = new CheckpointAcceptance(new GitProcess(fakeGit), records);

        await acceptance.AcceptAsync(
            "accepted-checkpoint",
            state,
            new WriteCheckpointRequest(
                "Progress",
                ["Implemented"],
                ["README.md"],
                ["Need review"],
                "Run tests"
            ),
            CancellationToken.None
        );

        var checkpoint = records.CheckpointAttempts.Should().ContainSingle().Which.Checkpoint;
        checkpoint.ChangedFiles.Should().Equal("new.txt", "src/A.cs", "src/B.cs");
        checkpoint.Outcomes.Should().ContainSingle().Which.Delivered.Should().BeFalse();
        checkpoint.AcceptedConstraints.Should().Equal("packet constraint", "planner constraint");
        checkpoint.InspectedFiles.Should().Equal("README.md");
        checkpoint.Uncertainties.Should().Equal("Need review");
        checkpoint.NextAction.Should().Be("Run tests");
    }

    [Fact]
    public async Task Publication_RetryAfterPushReconcilesExactBranchAndPersistsOnce()
    {
        using var temp = TempDir.Create();
        var branchState = Path.Combine(temp.Path, "branch-created");
        const string candidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var fakeGit = await CreateExecutableAsync(
            temp.Path,
            "publication-git.sh",
            $"""
            #!/bin/sh
            if [ "$1" = "check-ref-format" ] || [ "$1" = "cat-file" ]; then exit 0; fi
            if [ "$1" = "push" ]; then touch "{branchState}"; exit 0; fi
            if [ "$1" = "rev-parse" ] && [ "$2" = "HEAD" ]; then echo "{candidateSha}"; exit 0; fi
            if [ "$1" = "rev-parse" ] && [ "$2" = "--verify" ]; then
              if [ -f "{branchState}" ]; then echo "{candidateSha}"; exit 0; else exit 1; fi
            fi
            exit 1
            """
        );
        var records = new FakeDeliveryRecordSink
        {
            FailPublicationResults = true,
            PublicationCandidate = new PublicationCandidateDocument(
                "candidate",
                temp.Path,
                temp.Path,
                "Packet",
                "base",
                candidateSha
            ),
        };
        var operation = new PublicationOperation(new GitProcess(fakeGit), records);

        var first = async () => await operation.ExecuteAsync("tandem/test", CancellationToken.None);

        await first.Should().ThrowAsync<IOException>();
        File.Exists(branchState).Should().BeTrue();
        records.FailPublicationResults = false;

        var result = await operation.ExecuteAsync("tandem/test", CancellationToken.None);

        result.CandidateSha.Should().Be(candidateSha);
        records.PublicationResults.Should().ContainSingle();
    }

    [Fact]
    public async Task Publication_ExistingBranchAtAnotherCandidateConflicts()
    {
        using var temp = TempDir.Create();
        const string candidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string otherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var fakeGit = await CreateExecutableAsync(
            temp.Path,
            "publication-conflict-git.sh",
            $"""
            #!/bin/sh
            if [ "$1" = "check-ref-format" ] || [ "$1" = "cat-file" ]; then exit 0; fi
            if [ "$1" = "rev-parse" ] && [ "$2" = "HEAD" ]; then echo "{candidateSha}"; exit 0; fi
            if [ "$1" = "rev-parse" ] && [ "$2" = "--verify" ]; then echo "{otherSha}"; exit 0; fi
            exit 1
            """
        );
        var records = new FakeDeliveryRecordSink
        {
            PublicationCandidate = new PublicationCandidateDocument(
                "candidate",
                temp.Path,
                temp.Path,
                "Packet",
                "base",
                candidateSha
            ),
        };
        var operation = new PublicationOperation(new GitProcess(fakeGit), records);

        var publish = async () =>
            await operation.ExecuteAsync("tandem/conflict", CancellationToken.None);

        await publish
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*not candidate*");
        records.PublicationResults.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentPublication_ConvergesOnOneSQLiteRecord()
    {
        using var temp = TempDir.Create();
        var branchState = Path.Combine(temp.Path, "concurrent-branch-created");
        const string candidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var fakeGit = await CreateExecutableAsync(
            temp.Path,
            "publication-concurrent-git.sh",
            $"""
            #!/bin/sh
            if [ "$1" = "check-ref-format" ] || [ "$1" = "cat-file" ]; then exit 0; fi
            if [ "$1" = "push" ]; then touch "{branchState}"; exit 0; fi
            if [ "$1" = "rev-parse" ] && [ "$2" = "HEAD" ]; then echo "{candidateSha}"; exit 0; fi
            if [ "$1" = "rev-parse" ] && [ "$2" = "--verify" ]; then
              if [ -f "{branchState}" ]; then echo "{candidateSha}"; exit 0; else exit 1; fi
            fi
            exit 1
            """
        );
        var store = new SqliteLedgerStore(Path.Combine(temp.Path, "ledger.sqlite3"));
        await store.InitializeAsync();
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var records = new DeliveryLedger(store.ForRun(runId));
        var candidate = new PublicationCandidateDocument(
            "candidate",
            temp.Path,
            temp.Path,
            "Packet",
            "base",
            candidateSha
        );
        await records.AcceptPublicationCandidateAsync(
            "candidate",
            candidate,
            CancellationToken.None
        );
        var first = new PublicationOperation(new GitProcess(fakeGit), records);
        var second = new PublicationOperation(new GitProcess(fakeGit), records);

        var results = await Task.WhenAll(
            first.ExecuteAsync("tandem/concurrent", CancellationToken.None).AsTask(),
            second.ExecuteAsync("tandem/concurrent", CancellationToken.None).AsTask()
        );

        results.Should().OnlyContain(result => result.CandidateSha == candidateSha);
        var persisted = await store
            .ForRun(runId)
            .ReadAsync(
                new LedgerStream<PublicationResultRecord>(
                    "delivery.publication-results",
                    "delivery.publication-result"
                )
            );
        persisted.Should().ContainSingle();
    }

    [Fact]
    public async Task Verification_RecordsActualIndexAndInternalTimeoutEvidence()
    {
        using var temp = TempDir.Create();
        var block = new VerificationOperation(
            new GitProcess(),
            new FakeDeliveryRecordSink(),
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
        var block = new VerificationOperation(
            new GitProcess(),
            new FakeDeliveryRecordSink(),
            commandTimeout: TimeSpan.FromMinutes(1)
        );
        var message = CreateMessage(temp.Path, ["sleep 30"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => block.ExecuteAsync(Context(message), cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task VerificationPersistenceFailure_PreventsResultEnteringState()
    {
        using var temp = TempDir.Create();
        var records = new FakeDeliveryRecordSink { FailVerificationResults = true };
        var operation = new VerificationOperation(new GitProcess(), records);
        var message = CreateMessage(temp.Path, ["false"]);

        var execute = async () =>
            await operation.ExecuteAsync(Context(message), CancellationToken.None);

        await execute.Should().ThrowAsync<IOException>();
        message.State.VerificationResults.Should().BeEmpty();
        records.VerificationAttempts.Should().ContainSingle();
        records.VerificationAttempts[0].AcceptedResultId.Should().EndWith("--verify--1");
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

        var result = await new VerificationOperation(
            new GitProcess(),
            new FakeDeliveryRecordSink()
        ).ExecuteAsync(Context(message), CancellationToken.None);

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
