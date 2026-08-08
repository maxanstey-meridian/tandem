using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class PlannerAgent
{
    internal static AgentDefinition<DeliveryState> Create(DeliveryAgentFactory agents) =>
        agents.Create(
            DeliveryIds.Planner,
            "planning",
            PlannerPrompts.Instructions,
            builder =>
                builder
                    .WithMessage(PlannerPrompts.BuildMessage)
                    .WithOutput(
                        new PlannerDecisionOutput(),
                        (state, decision) => state.RecordPlannerDecision(decision)
                    )
                    .RequireOutputAcceptance(PlannerPolicies.RepositoryGrounded())
                    .WithOutputAcceptance<DeliveryState, PlannerDecision>(
                        (observation, cancellationToken) =>
                            agents.Records.AcceptPlannerDecisionAsync(
                                observation.AcceptedOutputId,
                                observation.Output,
                                cancellationToken
                            )
                    )
                    .ContinueSession()
        );
}
