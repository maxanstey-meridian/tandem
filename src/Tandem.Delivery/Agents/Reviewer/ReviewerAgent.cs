using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class ReviewerAgent
{
    internal static AgentDefinition<DeliveryState> Create(
        DeliveryAgentFactory agents,
        DeliveryDiffAcquisition diffAcquisition
    ) =>
        agents.Create(
            BlockIds.Reviewer,
            "review",
            ReviewerPrompts.Instructions,
            builder =>
                builder
                    .WithMessage(ReviewerPrompts.BuildMessage)
                    .WithOutput<DeliveryState, ReviewDecision>(
                        ReviewDecisionPolicy.Parse,
                        ReviewerPolicies.RequireRepositoryGrounding(),
                        "file_access_read"
                    )
                    .WithMessageAugmentation(ReviewerPolicies.IncludeCandidateDiff(diffAcquisition))
                    .WithConversationPolicy(ReviewerPolicies.DiscardAfterDecision)
        );
}
