using Microsoft.Extensions.AI;
using Tandem.Advanced;

namespace Tandem.Delivery;

internal sealed class DeliveryAgentFactory(
    Func<string, IChatClient> chatClients,
    Func<string, DeliveryAgentProfile> profileResolver
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
        var builder = AgentProfiles
            .Create<DeliveryState>(
                participantId,
                profileName,
                instructions,
                chatClients(profileName),
                chatClients
            )
            .UseHarness(DeliveryHarnessInstructions.Value)
            .WithWorkspace(state => state.WorkspacePath, _ => false);

        return configure(builder).Build();
    }

    internal DeliveryAgentProfile ResolveProfile(string profileName) =>
        profileResolver(profileName);
}
