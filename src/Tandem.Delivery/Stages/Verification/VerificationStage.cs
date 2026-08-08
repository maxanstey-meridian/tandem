using Tandem.Advanced;

namespace Tandem.Delivery;

[PipelineStage(DeliveryIds.Verify)]
public sealed partial class VerificationStage(VerificationOperation operation)
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
                    : StageOutcome.Unexpected(result, DeliveryIds.Verify)
        );
}
