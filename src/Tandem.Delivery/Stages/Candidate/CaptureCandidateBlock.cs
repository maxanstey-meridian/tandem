using System.Diagnostics;
using System.Text.Json;
using Tandem.Advanced;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed class CaptureCandidateBlock(GitProcess git)
{
    public async ValueTask<OperationResult<DeliveryState>> ExecuteAsync(
        PipelineOperationContext<DeliveryState> context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = context.State;
        var ws = ctx.WorkspacePath;

        var addResult = await git.RunAsync(ws, ["add", "-A"], cancellationToken);
        EnsureSucceeded("git add", addResult, cancellationToken);

        var commitResult = await git.RunAsync(
            ws,
            [
                "-c",
                "user.name=Tandem",
                "-c",
                "user.email=tandem@localhost",
                "commit",
                "--allow-empty",
                "-m",
                $"Tandem candidate {context.RunId:N}",
            ],
            cancellationToken
        );
        EnsureSucceeded("git commit", commitResult, cancellationToken);

        var revResult = await git.RunAsync(ws, ["rev-parse", "HEAD"], cancellationToken);
        EnsureSucceeded("git rev-parse", revResult, cancellationToken);
        var candidateSha = revResult.Stdout.Trim();

        var updatedContext = ctx with
        {
            CandidateSha = candidateSha,
            VerificationIndex = 0,
            VerificationResults = [],
        };

        var payload = JsonSerializer.SerializeToElement(new { candidateSha });
        sw.Stop();
        return new OperationResult<DeliveryState>(
            updatedContext,
            new OperationOutcome(
                OutcomeKinds.CandidateCaptured,
                BlockIds.CaptureCandidate,
                "Candidate captured",
                payload,
                sw.Elapsed
            )
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
