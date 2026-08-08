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
                    .WithWorkspace(
                        state => state.WorkspacePath,
                        state => state.MutationAuthorized,
                        ExecutorPolicies.CreateMutationGate()
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
