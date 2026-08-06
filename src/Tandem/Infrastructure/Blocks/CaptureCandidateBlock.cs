using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class CaptureCandidateBlock(GitProcess? git = null)
    : Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>>(
        BlockIds.CaptureCandidate
    )
{
    private readonly GitProcess _git = git ?? new GitProcess();

    public override async ValueTask<PipelineMessage<SimpleV1State>> HandleAsync(
        PipelineMessage<SimpleV1State> message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = message.State;
        var ws = ctx.WorkspacePath;

        await _git.RunAsync(ws, ["add", "-A"], cancellationToken);
        await _git.RunAsync(
            ws,
            [
                "-c",
                "user.name=Tandem",
                "-c",
                "user.email=tandem@localhost",
                "commit",
                "--allow-empty",
                "-m",
                $"Tandem candidate {message.Runtime.RunId:N}",
            ],
            cancellationToken
        );

        var revResult = await _git.RunAsync(ws, ["rev-parse", "HEAD"], cancellationToken);
        var candidateSha = revResult.Stdout.Trim();

        var updatedContext = ctx with
        {
            CandidateSha = candidateSha,
            VerificationIndex = 0,
            VerificationResults = [],
        };

        var payload = JsonSerializer.SerializeToElement(new { candidateSha });
        sw.Stop();
        return new PipelineMessage<SimpleV1State>(
            message.Runtime,
            updatedContext,
            new BlockOutcome(
                OutcomeKinds.CandidateCaptured,
                BlockIds.CaptureCandidate,
                "Candidate captured",
                payload,
                sw.Elapsed
            )
        );
    }
}
