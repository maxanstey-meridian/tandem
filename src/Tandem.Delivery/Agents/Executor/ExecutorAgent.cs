using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class ExecutorAgent
{
    internal static AgentDefinition<DeliveryState> Create(
        DeliveryAgentFactory agents,
        AgentCapability<DeliveryState> askPlanner,
        AgentCapability<DeliveryState> submitReport,
        AgentCapability<DeliveryState> writeCheckpoint
    ) =>
        agents.Create(
            DeliveryIds.Executor,
            "implementation",
            ExecutorPrompts.Instructions,
            builder =>
                builder
                    .WithCapability(askPlanner)
                    .WithCapability(submitReport)
                    .WithMessage(ExecutorPrompts.BuildMessage)
                    .WithWorkspace(state => state.WorkspacePath, state => state.MutationAuthorized)
                    .WithStateGuard(
                        new AgentStateGuard<DeliveryState>(
                            "planner-authorization",
                            state => !state.MutationAuthorized,
                            new HashSet<ToolEffect> { ToolEffect.WorkspaceMutation },
                            """
                            MUTATION GATE CLOSED: Your edit was NOT applied — no file was changed.
                            Mutation authority is not yet granted. Call ask_planner with your
                            proposed approach and evidence. Reads remain available for gathering
                            evidence. Continue only on proceed or proceed_with_constraints.
                            """,
                            askPlanner
                        )
                    )
                    .WithCheckpoint(
                        CreateCheckpointPolicy(
                            agents.ResolveProfile("implementation"),
                            writeCheckpoint
                        )
                    )
                    .WithContinuationPolicy(ExecutorPolicies.CreateTurnPolicy())
                    .ContinueSession()
                    .WithConversationPolicy(ExecutorPolicies.RetainUntilAcceptedReport)
        );

    private static CheckpointPolicy<DeliveryState> CreateCheckpointPolicy(
        DeliveryAgentProfile profile,
        AgentCapability<DeliveryState> writeCheckpoint
    ) =>
        new(
            profile.ContextWindowTokens,
            profile.MaxOutputTokens,
            profile.CheckpointAtPercent,
            writeCheckpoint,
            ExecutorPrompts.CheckpointInstructions,
            ExecutorPrompts.BuildCheckpointMessage
        );
}
