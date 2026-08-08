using Tandem.Git;

namespace Tandem.Delivery;

[PipelineStage(DeliveryIds.CaptureCandidate)]
public sealed partial class CaptureCandidateStage(GitProcess git, IDeliveryRecordSink records)
{
    public async ValueTask<Outcome<DeliveryState>> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    )
    {
        var addResult = await git.RunAsync(state.WorkspacePath, ["add", "-A"], cancellationToken);
        EnsureSucceeded("git add", addResult, cancellationToken);
        var commitResult = await git.RunAsync(
            state.WorkspacePath,
            [
                "-c",
                "user.name=Tandem",
                "-c",
                "user.email=tandem@localhost",
                "commit",
                "--allow-empty",
                "-m",
                "Tandem candidate",
            ],
            cancellationToken
        );
        EnsureSucceeded("git commit", commitResult, cancellationToken);
        var revResult = await git.RunAsync(
            state.WorkspacePath,
            ["rev-parse", "HEAD"],
            cancellationToken
        );
        EnsureSucceeded("git rev-parse", revResult, cancellationToken);
        var candidateSha = revResult.Stdout.Trim();
        var acceptedCandidateId = $"candidate--{candidateSha}";
        await records.AcceptPublicationCandidateAsync(
            acceptedCandidateId,
            new PublicationCandidateDocument(
                acceptedCandidateId,
                state.Packet.Repository,
                state.WorkspacePath,
                state.Packet.Title,
                state.PinnedBaseSha,
                candidateSha
            ),
            cancellationToken
        );
        return new Outcome<DeliveryState>.Success(
            state with
            {
                CandidateSha = candidateSha,
                VerificationIndex = 0,
                VerificationResults = [],
            }
        );
    }

    private static void EnsureSucceeded(
        string operation,
        GitResult result,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode == 0 && !result.TimedOut)
        {
            return;
        }
        var evidence = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout.Trim()
            : result.Stderr.Trim();
        throw new InvalidOperationException(
            $"{operation} failed (exit code {result.ExitCode}, timed out: {result.TimedOut}). {evidence}"
        );
    }
}
