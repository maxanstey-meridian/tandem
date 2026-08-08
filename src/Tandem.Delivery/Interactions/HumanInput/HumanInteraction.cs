namespace Tandem.Delivery;

public static class HumanInteraction
{
    public static HumanQuestion BuildPlannerQuestion(DeliveryState state) =>
        state.PlannerDecision is { Decision: PlannerDecisionValue.NeedsHuman } planner
            ? new HumanQuestion(planner.HumanQuestion ?? "No question provided.", planner.Rationale)
            : throw new InvalidOperationException("No pending planner question exists.");

    public static DeliveryState ApplyPlannerAnswer(DeliveryState state, HumanAnswer answer) =>
        state with
        {
            PlannerDecision = null,
            PlannerHumanAnswer = answer.Text,
        };

    public static HumanQuestion BuildReviewerQuestion(DeliveryState state) =>
        state.ReviewerDecision is { Decision: ReviewDecisionValue.NeedsHuman } reviewer
            ? new HumanQuestion(reviewer.HumanQuestion ?? "No question provided.", reviewer.Summary)
            : throw new InvalidOperationException("No pending reviewer question exists.");

    public static DeliveryState ApplyReviewerAnswer(DeliveryState state, HumanAnswer answer) =>
        state with
        {
            ReviewerDecision = null,
            ReviewerHumanAnswer = answer.Text,
        };
}
