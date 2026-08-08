using Microsoft.Extensions.AI;
using Tandem.Advanced;

namespace Tandem.Delivery;

internal sealed class DeliveryAgentFactory(
    Func<string, IChatClient> chatClients,
    Func<string, DeliveryAgentProfile> profileResolver,
    IDeliveryRecordSink records
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
            .WithWorkspace(state => state.WorkspacePath, _ => false)
            .WithMessageAugmentation(
                async (_, cancellationToken) =>
                    DeliveryLedgerContextFormatter.Format(
                        await records.ReadContextAsync(
                            participantId switch
                            {
                                DeliveryIds.Executor => DeliveryLedgerRole.Executor,
                                DeliveryIds.Planner => DeliveryLedgerRole.Planner,
                                DeliveryIds.Reviewer => DeliveryLedgerRole.Reviewer,
                                _ => throw new InvalidOperationException(
                                    $"Unknown Delivery agent '{participantId}'."
                                ),
                            },
                            cancellationToken
                        )
                    )
            );

        return configure(builder).Build();
    }

    internal DeliveryAgentProfile ResolveProfile(string profileName) =>
        profileResolver(profileName);

    internal IDeliveryRecordSink Records => records;
}
