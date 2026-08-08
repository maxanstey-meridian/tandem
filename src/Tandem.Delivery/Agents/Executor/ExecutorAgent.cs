using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class ExecutorAgent
{
    internal static AgentDefinition<DeliveryState> Create(DeliveryAgentFactory agents) =>
        agents.Create(
            BlockIds.Executor,
            "implementation",
            ExecutorPrompts.Instructions,
            builder =>
                agents
                    .AddExecutorCapabilities(builder)
                    .WithMessage(ExecutorPrompts.BuildMessage)
                    .WithWorkspace(
                        state => state.WorkspacePath,
                        state => ExecutorPolicies.AllowsWorkspaceMutation(BlockIds.Executor, state),
                        ExecutorPolicies.CreateMutationGate()
                    )
                    .WithContinuationPolicy(ExecutorPolicies.CreateTurnPolicy())
                    .ContinueSession()
                    .WithConversationPolicy(ExecutorPolicies.RetainUntilAcceptedReport)
        );
}
