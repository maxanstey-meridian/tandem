using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Advanced;

namespace Tandem.Delivery;

public static class PlannerDecisionPolicy
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
            new PlannerDecisionValidator(),
            Map
        );

    private static StructuredOutcome<DeliveryState> Map(
        PlannerDecision decision,
        DeliveryState state
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
        var authorizesMutation =
            decision.Decision
            is PlannerDecisionValue.Proceed
                or PlannerDecisionValue.ProceedWithConstraints;
        return new StructuredOutcome<DeliveryState>(
            kind,
            decision.Rationale,
            JsonSerializer.SerializeToElement(decision, _jsonOptions),
            state with
            {
                PlannerDecision = decision,
                PlannerConstraints =
                    decision.Constraints.Count > 0
                        ? decision.Constraints
                        : state.PlannerConstraints,
                MutationAuthorized = authorizesMutation || state.MutationAuthorized,
                PlannerHumanAnswer = null,
                Status =
                    decision.Decision == PlannerDecisionValue.NeedsHuman
                        ? RunStatus.WaitingForHuman
                        : state.Status,
            }
        );
    }
}
