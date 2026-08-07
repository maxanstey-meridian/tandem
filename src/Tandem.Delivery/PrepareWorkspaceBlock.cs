using System.Diagnostics;
using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Delivery;

public sealed class PrepareWorkspaceBlock(WorkspacePreparation preparation)
{
    public async ValueTask<PipelineMessage<DeliveryState>> ExecuteAsync(
        PipelineMessage<DeliveryState> message,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = message.State;
        var runDir = Path.GetDirectoryName(ctx.WorkspacePath)!;

        var prep = await preparation.PrepareAsync(
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
