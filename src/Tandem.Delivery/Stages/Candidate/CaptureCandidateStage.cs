using Tandem.Advanced;

namespace Tandem.Delivery;

[PipelineStage(BlockIds.CaptureCandidate)]
public sealed partial class CaptureCandidateStage(CaptureCandidateBlock operation)
{
    public ValueTask<Outcome<DeliveryState>> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            pipeline => operation.ExecuteAsync(pipeline, cancellationToken),
            result =>
                StageOutcome.Expected(
                    result,
                    OutcomeKinds.CandidateCaptured,
                    BlockIds.CaptureCandidate
                )
        );
}
