using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class PlannerAgent
{
    internal static AgentDefinition<DeliveryState> Create(DeliveryAgentFactory agents) =>
        agents.Create(
            BlockIds.Planner,
            "planning",
            PlannerPrompts.Instructions,
            builder =>
                builder
                    .WithMessageFromContext(PlannerPrompts.BuildMessage)
                    .WithOutput<DeliveryState, PlannerDecision>(
                        PlannerDecisionPolicy.Parse,
                        PlannerPolicies.RequireRepositoryGrounding(),
                        "file_access_read"
                    )
                    .ContinueSession()
        );
}
