using Microsoft.Extensions.AI;
using Tandem.Advanced;

namespace Tandem.Delivery;

internal sealed class DeliveryAgentFactory(
    AgentFactory agentFactory,
    Func<string, IChatClient> chatClients,
    Func<string, DeliveryAgentProfile> profileResolver,
    AgentCapability<DeliveryState> askPlanner,
    AgentCapability<DeliveryState> submitReport,
    AgentCapability<DeliveryState> writeCheckpoint
)
{
    internal AgentDefinition<DeliveryState> Create(
        string participantId,
        string profileName,
        string instructions,
        Func<AgentBuilder<DeliveryState>, AgentBuilder<DeliveryState>> configure
    )
    {
        var profile = profileResolver(profileName);
        var builder = agentFactory
            .CreateProfiled<DeliveryState>(
                participantId,
                profileName,
                instructions,
                chatClients(profileName),
                chatClients
            )
            .UseHarness(DeliveryHarnessInstructions.Value)
            .WithWorkspace(
                state => state.WorkspacePath,
                state => ExecutorPolicies.AllowsWorkspaceMutation(participantId, state)
            );

        if (ExecutorPolicies.OwnsCheckpoint(participantId))
        {
            builder.WithCheckpoint(
                new CheckpointPolicy<DeliveryState>(
                    profile.ContextWindowTokens,
                    profile.MaxOutputTokens,
                    profile.CheckpointAtPercent,
                    writeCheckpoint,
                    ExecutorPrompts.CheckpointInstructions,
                    ExecutorPrompts.BuildCheckpointMessage
                )
            );
        }

        return configure(builder).Build();
    }

    internal AgentBuilder<DeliveryState> AddExecutorCapabilities(
        AgentBuilder<DeliveryState> builder
    ) => builder.WithCapability(askPlanner).WithCapability(submitReport);
}
