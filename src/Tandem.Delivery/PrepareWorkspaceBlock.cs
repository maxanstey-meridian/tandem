using System.Diagnostics;
using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class PrepareWorkspaceBlock(WorkspacePreparation? preparation = null)
{
    private readonly WorkspacePreparation _preparation = preparation ?? new WorkspacePreparation();

    public async ValueTask<PipelineMessage<DeliveryState>> ExecuteAsync(
        PipelineMessage<DeliveryState> message,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = message.State;
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
        return new PipelineMessage<DeliveryState>(
            message.Runtime,
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
