using Tandem.Domain;

namespace Tandem.Delivery;

public static class HumanInteraction
{
    public static HumanQuestion BuildQuestion(DeliveryState state)
    {
        if (state.PlannerDecision is { Decision: PlannerDecisionValue.NeedsHuman } planner)
        {
            return new HumanQuestion(
                BlockIds.Planner,
                planner.HumanQuestion ?? "No question provided.",
                planner.Rationale
            );
        }
        if (state.ReviewerDecision is { Decision: ReviewDecisionValue.NeedsHuman } reviewer)
        {
            return new HumanQuestion(
                BlockIds.Reviewer,
                reviewer.HumanQuestion ?? "No question provided.",
                reviewer.Summary
            );
        }
        throw new InvalidOperationException("No pending human question exists in delivery state.");
    }

    public static DeliveryState ApplyAnswer(DeliveryState state, HumanAnswer answer)
    {
        var source = BuildQuestion(state).SourceBlockId;
        return state with
        {
            PlannerDecision = null,
            ReviewerDecision = null,
            ReviewerHumanAnswer = source == BlockIds.Reviewer ? answer.Text : null,
            HumanAnswerSourceBlockId = source,
            Status = RunStatus.Running,
        };
    }
}
