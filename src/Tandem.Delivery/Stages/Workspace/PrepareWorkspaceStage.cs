namespace Tandem.Delivery;

[PipelineStage(DeliveryIds.Prepare)]
public sealed partial class PrepareWorkspaceStage(WorkspacePreparation preparation)
{
    public async ValueTask<Outcome<DeliveryState>> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    )
    {
        var prepared = await preparation.PrepareAsync(
            state.Packet,
            state.WorkspacePath,
            cancellationToken
        );
        return new Outcome<DeliveryState>.Success(
            state with
            {
                PinnedBaseSha = prepared.PinnedBaseSha,
            }
        );
    }
}
