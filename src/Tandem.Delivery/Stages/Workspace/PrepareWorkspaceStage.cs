using Tandem.Advanced;

namespace Tandem.Delivery;

[PipelineStage(BlockIds.Prepare)]
public sealed partial class PrepareWorkspaceStage(PrepareWorkspaceBlock operation)
{
    public ValueTask<Outcome<DeliveryState>> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            pipeline => operation.ExecuteAsync(pipeline, cancellationToken),
            result =>
                StageOutcome.Expected(result, OutcomeKinds.WorkspacePrepared, BlockIds.Prepare)
        );
}
