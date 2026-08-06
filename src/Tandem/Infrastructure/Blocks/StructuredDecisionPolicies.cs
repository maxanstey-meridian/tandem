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

    public static StructuredOutputResult Parse(string response, PipelineContext context) =>
        StructuredOutputPolicy.Parse(
            response,
            context,
            _jsonOptions,
            new PlannerDecisionValidator(),
            Map
        );

    private static StructuredOutcome Map(PlannerDecision decision, PipelineContext context)
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
        var updatedContext = context with
        {
            PlannerDecision = decision,
            PlannerConstraints =
                decision.Constraints.Count > 0 ? decision.Constraints : context.PlannerConstraints,
            MutationAuthorized = authorizesMutation || context.MutationAuthorized,
        };
        return new StructuredOutcome(kind, decision.Rationale, payload, updatedContext);
    }
}

public static class ReviewDecisionPolicy
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static StructuredOutputResult Parse(string response, PipelineContext context) =>
        StructuredOutputPolicy.Parse(
            response,
            context,
            _jsonOptions,
            new ReviewDecisionValidator(),
            Map
        );

    private static StructuredOutcome Map(ReviewDecision decision, PipelineContext context)
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
        return new StructuredOutcome(kind, decision.Summary, payload);
    }
}
