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
                        state => new ReviewDecisionValidator(
                            state.Packet.Outcomes.Select(outcome => outcome.Id)
                        ),
                        ReviewerPolicies.ApplyDecision
                    )
                    .RequireOutputAcceptance(ReviewerPolicies.RepositoryGrounded())
                    .WithOutputAcceptance<DeliveryState, ReviewDecision>(
                        (observation, cancellationToken) =>
                            agents.Records.AcceptReviewDecisionAsync(
                                observation.AcceptedOutputId,
                                observation.Output,
                                cancellationToken
                            )
                    )
                    .WithMessageAugmentation(ReviewerPolicies.IncludeCandidateDiff(diffAcquisition))
                    .WithConversationPolicy(ReviewerPolicies.DiscardAfterDecision)
        );
}
