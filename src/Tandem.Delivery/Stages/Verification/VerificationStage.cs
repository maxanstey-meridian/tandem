using Tandem.Advanced;

namespace Tandem.Delivery;

[PipelineStage(BlockIds.Verify)]
public sealed partial class VerificationStage(VerificationBlock operation)
{
    public ValueTask<Outcome<DeliveryState>> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            pipeline => operation.ExecuteAsync(pipeline, cancellationToken),
            result =>
                result.Outcome.Kind is OutcomeKinds.CommandPassed or OutcomeKinds.CommandFailed
                    ? new Outcome<DeliveryState>.Success(result.State)
                    : StageOutcome.Unexpected(result, BlockIds.Verify)
        );
}
