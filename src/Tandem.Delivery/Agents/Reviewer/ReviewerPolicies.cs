using Tandem.Advanced;

namespace Tandem.Delivery;

public static class ReviewerPolicies
{
    public static AgentConversationDecision DiscardAfterDecision(
        AgentMessageContext<DeliveryState> _,
        AgentMessageOutcome __
    ) => new(AgentConversationRetention.Discard);

    public static StructuredOutputAcceptancePolicy<DeliveryState> RequireRepositoryGrounding() =>
        StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
            result =>
                result.Outcome?.Kind
                    is OutcomeKinds.ReviewAccepted
                        or OutcomeKinds.ReviewChangesRequested
                || result.Candidate
                    is ReviewDecision
                    {
                        Decision: ReviewDecisionValue.Accept or ReviewDecisionValue.RequestChanges,
                    },
            RepositoryGrounding.IsInspectionTool,
            correction: "Accept and RequestChanges require repository inspection in this review. "
                + "Use an available read-only repository tool to verify the candidate and packet outcomes, "
                + "then return only the corrected JSON decision with concrete outcome evidence."
        );

    public static MessageAugmentation<DeliveryState> IncludeCandidateDiff(
        DeliveryDiffAcquisition diffAcquisition
    ) =>
        async (context, cancellationToken) =>
            context.State.CandidateSha is null || string.IsNullOrEmpty(context.State.PinnedBaseSha)
                ? null
                : await diffAcquisition.AcquireAsync(context.State, cancellationToken);
}
