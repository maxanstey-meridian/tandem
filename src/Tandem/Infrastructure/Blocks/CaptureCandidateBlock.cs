using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class CaptureCandidateBlock : Executor<PipelineMessage, PipelineMessage>
{
    private readonly GitProcess _git;

    public CaptureCandidateBlock(GitProcess? git = null)
        : base(BlockIds.CaptureCandidate)
    {
        _git = git ?? new GitProcess();
    }

    public override async ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var ctx = message.Context;
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
                $"Tandem candidate {ctx.RunId:N}",
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
        return new PipelineMessage(
            updatedContext,
            new BlockOutcome(
                OutcomeKinds.CandidateCaptured,
                BlockIds.CaptureCandidate,
                "Candidate captured",
                payload
            )
        );
    }
}
