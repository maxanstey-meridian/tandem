using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed record DeliveryAgentProfile(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent
);

public sealed record DeliveryOptions(
    Func<string, IChatClient> ChatClients,
    Func<string, DeliveryAgentProfile> Profiles,
    IDeliveryRecordSink Records
);

public static class DeliveryRegistration
{
    public static IServiceCollection AddDelivery(
        this IServiceCollection services,
        DeliveryOptions options
    )
    {
        services.AddSingleton(options.Records);
        services.TryAddSingleton<GitProcess>();
        services.AddSingleton<CheckpointAcceptance>();
        services.AddSingleton(sp =>
            DeliveryCapabilities.Create(
                options.Records,
                sp.GetRequiredService<CheckpointAcceptance>()
            )
        );
        services.AddSingleton<AgentCapability<DeliveryState>>(sp =>
            sp.GetRequiredService<DeliveryCapabilitySet>().AskPlanner
        );
        services.AddSingleton<AgentCapability<DeliveryState>>(sp =>
            sp.GetRequiredService<DeliveryCapabilitySet>().SubmitReport
        );
        services.AddSingleton<AgentCapability<DeliveryState>>(sp =>
            sp.GetRequiredService<DeliveryCapabilitySet>().WriteCheckpoint
        );
        services.AddSingleton<WorkspacePreparation>();
        services.AddSingleton<DeliveryDiffAcquisition>();
        services.AddSingleton<DeliveryParticipantsFactory>(sp =>
        {
            var capabilities = sp.GetRequiredService<DeliveryCapabilitySet>();
            return new DeliveryParticipantsFactory(
                options.ChatClients,
                options.Profiles,
                options.Records,
                sp.GetRequiredService<DeliveryDiffAcquisition>(),
                sp.GetRequiredService<WorkspacePreparation>(),
                sp.GetRequiredService<GitProcess>(),
                capabilities.AskPlanner,
                capabilities.SubmitReport,
                capabilities.WriteCheckpoint
            );
        });
        services.AddSingleton<DeliveryComposition>();
        return services;
    }
}
