using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class StageOutcome
{
    internal static Outcome<DeliveryState> Expected(
        OperationResult<DeliveryState> result,
        string expected,
        string participantId
    ) =>
        result.Outcome.Kind == expected
            ? new Outcome<DeliveryState>.Success(result.State)
            : Unexpected(result, participantId);

    internal static Outcome<DeliveryState>.Failed Unexpected(
        OperationResult<DeliveryState> result,
        string participantId
    ) =>
        new(
            result.State,
            new FailureEvidence(
                "delivery.unexpected_outcome",
                $"Participant '{participantId}' produced unexpected outcome '{result.Outcome.Kind}'."
            )
        );
}
