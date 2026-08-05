using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class PrepareWorkspaceBlock : Executor<PipelineMessage, PipelineMessage>
{
    private readonly WorkspacePreparation _preparation;

    public PrepareWorkspaceBlock(WorkspacePreparation? preparation = null)
        : base(BlockIds.Prepare)
    {
        _preparation = preparation ?? new WorkspacePreparation();
    }

    public override async ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
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

        return new PipelineMessage(
            updatedContext,
            new BlockOutcome(
                OutcomeKinds.WorkspacePrepared,
                BlockIds.Prepare,
                "Workspace prepared",
                payload
            )
        );
    }
}
