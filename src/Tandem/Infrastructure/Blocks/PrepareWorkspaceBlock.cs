using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class PrepareWorkspaceBlock(WorkspacePreparation? preparation = null)
    : Executor<PipelineMessage, PipelineMessage>(BlockIds.Prepare)
{
    private readonly WorkspacePreparation _preparation = preparation ?? new WorkspacePreparation();

    public override async ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = message.Context;
        var runDir = Path.GetDirectoryName(ctx.WorkspacePath)!;

        var prep = await _preparation.PrepareAsync(
            ctx.Packet,
            runDir,
            ctx.WorkspacePath,
            cancellationToken
        );

        var updatedContext = ctx with
        {
            PinnedBaseSha = prep.PinnedBaseSha,
            Status = Domain.RunStatus.Running,
        };

        var payload = JsonSerializer.SerializeToElement(
            new { pinnedSha = prep.PinnedBaseSha, workspacePath = prep.WorkspacePath }
        );

        sw.Stop();
        return new PipelineMessage(
            updatedContext,
            new BlockOutcome(
                OutcomeKinds.WorkspacePrepared,
                BlockIds.Prepare,
                "Workspace prepared",
                payload,
                sw.Elapsed
            )
        );
    }
}
