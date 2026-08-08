using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Advanced;

namespace Tandem.Delivery;

public static class ReviewDecisionPolicy
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static StructuredOutputResult<DeliveryState> Parse(
        string response,
        DeliveryState state
    ) =>
        StructuredOutputPolicy.Parse(
            response,
            state,
            _jsonOptions,
            new ReviewDecisionValidator(state.Packet.Outcomes.Select(outcome => outcome.Id)),
            Map
        );

    private static StructuredOutcome<DeliveryState> Map(
        ReviewDecision decision,
        DeliveryState state
    )
    {
        var kind = decision.Decision switch
        {
            ReviewDecisionValue.Accept => OutcomeKinds.ReviewAccepted,
            ReviewDecisionValue.RequestChanges => OutcomeKinds.ReviewChangesRequested,
            ReviewDecisionValue.NeedsHuman => OutcomeKinds.ReviewNeedsHuman,
            _ => throw new InvalidOperationException(
                $"Unknown review decision: {decision.Decision}"
            ),
        };
        return new StructuredOutcome<DeliveryState>(
            kind,
            decision.Summary,
            JsonSerializer.SerializeToElement(decision, _jsonOptions),
            state with
            {
                ReviewerDecision = decision,
                ReviewerHumanAnswer = null,
                Status =
                    decision.Decision == ReviewDecisionValue.NeedsHuman
                        ? RunStatus.WaitingForHuman
                        : state.Status,
            }
        );
    }
}
