using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public static class PlannerDecisionPolicy
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static StructuredOutputResult<DeliveryState> Parse(
        string response,
        PipelineMessage<DeliveryState> message
    ) =>
        StructuredOutputPolicy.Parse(
            response,
            message,
            _jsonOptions,
            new PlannerDecisionValidator(),
            Map
        );

    private static StructuredOutcome<DeliveryState> Map(
        PlannerDecision decision,
        PipelineMessage<DeliveryState> message
    )
    {
        var kind = decision.Decision switch
        {
            PlannerDecisionValue.Proceed => OutcomeKinds.PlannerProceed,
            PlannerDecisionValue.ProceedWithConstraints =>
                OutcomeKinds.PlannerProceedWithConstraints,
            PlannerDecisionValue.NeedsHuman => OutcomeKinds.PlannerNeedsHuman,
            PlannerDecisionValue.Stop => OutcomeKinds.PlannerStop,
            _ => throw new InvalidOperationException(
                $"Unknown planner decision: {decision.Decision}"
            ),
        };
        var payload = JsonSerializer.SerializeToElement(decision, _jsonOptions);
        var authorizesMutation =
            decision.Decision
            is PlannerDecisionValue.Proceed
                or PlannerDecisionValue.ProceedWithConstraints;
        var state = message.State;
        var updatedState = state with
        {
            PlannerDecision = decision,
            PlannerConstraints =
                decision.Constraints.Count > 0 ? decision.Constraints : state.PlannerConstraints,
            MutationAuthorized = authorizesMutation || state.MutationAuthorized,
        };
        return new StructuredOutcome<DeliveryState>(
            kind,
            decision.Rationale,
            payload,
            updatedState
        );
    }
}

public static class ReviewDecisionPolicy
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static StructuredOutputResult<DeliveryState> Parse(
        string response,
        PipelineMessage<DeliveryState> message
    ) =>
        StructuredOutputPolicy.Parse(
            response,
            message,
            _jsonOptions,
            new ReviewDecisionValidator(
                message.State.Packet.Outcomes.Select(outcome => outcome.Id)
            ),
            Map
        );

    private static StructuredOutcome<DeliveryState> Map(
        ReviewDecision decision,
        PipelineMessage<DeliveryState> message
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
        var payload = JsonSerializer.SerializeToElement(decision, _jsonOptions);
        return new StructuredOutcome<DeliveryState>(
            kind,
            decision.Summary,
            payload,
            message.State with
            {
                ReviewerHumanAnswer = null,
            }
        );
    }
}
