using System.Diagnostics;
using System.Text.Json;
using Tandem.Advanced;

namespace Tandem.Delivery;

public sealed class PrepareWorkspaceBlock(WorkspacePreparation preparation)
{
    public async ValueTask<OperationResult<DeliveryState>> ExecuteAsync(
        PipelineOperationContext<DeliveryState> context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = context.State;

        var prep = await preparation.PrepareAsync(ctx.Packet, ctx.WorkspacePath, cancellationToken);

        var updatedContext = ctx with
        {
            PinnedBaseSha = prep.PinnedBaseSha,
            Status = RunStatus.Running,
        };

        var payload = JsonSerializer.SerializeToElement(
            new { pinnedSha = prep.PinnedBaseSha, workspacePath = prep.WorkspacePath }
        );

        sw.Stop();
        return new OperationResult<DeliveryState>(
            updatedContext,
            new OperationOutcome(
                OutcomeKinds.WorkspacePrepared,
                BlockIds.Prepare,
                "Workspace prepared",
                payload,
                sw.Elapsed
            )
        );
    }
}
