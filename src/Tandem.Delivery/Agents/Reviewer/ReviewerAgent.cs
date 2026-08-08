using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class ReviewerAgent
{
    internal static AgentDefinition<DeliveryState> Create(
        DeliveryAgentFactory agents,
        DeliveryDiffAcquisition diffAcquisition
    ) =>
        agents.Create(
            DeliveryIds.Reviewer,
            "review",
            ReviewerPrompts.Instructions,
            builder =>
                builder
                    .WithMessage(ReviewerPrompts.BuildMessage)
                    .WithOutput(
                        new ReviewDecisionOutput(),
                        (state, decision) => state.RecordReviewDecision(decision)
                    )
                    .RequireOutputAcceptance(ReviewerPolicies.RepositoryGrounded())
                    .WithMessageAugmentation(ReviewerPolicies.IncludeCandidateDiff(diffAcquisition))
                    .WithConversationPolicy(ReviewerPolicies.DiscardAfterDecision)
        );
}
