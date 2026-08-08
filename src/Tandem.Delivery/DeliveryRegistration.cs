using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed record DeliveryAgentProfile(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent
);

public sealed record DeliveryOptions(
    Func<string, IChatClient> ChatClients,
    Func<string, DeliveryAgentProfile> Profiles
);

public static class DeliveryRegistration
{
    public static IServiceCollection AddDelivery(
        this IServiceCollection services,
        DeliveryOptions options
    )
    {
        var capabilities = DeliveryCapabilities.Create();
        var askPlanner = capabilities.AskPlanner;
        var submitReport = capabilities.SubmitReport;
        var writeCheckpoint = capabilities.WriteCheckpoint;
        services.AddSingleton(askPlanner);
        services.AddSingleton(submitReport);
        services.AddSingleton(writeCheckpoint);
        services.AddSingleton<GitProcess>();
        services.AddSingleton<WorkspacePreparation>();
        services.AddSingleton<DeliveryDiffAcquisition>();
        services.AddSingleton<DeliveryParticipantsFactory>(sp =>
        {
            return new DeliveryParticipantsFactory(
                options.ChatClients,
                options.Profiles,
                sp.GetRequiredService<DeliveryDiffAcquisition>(),
                sp.GetRequiredService<WorkspacePreparation>(),
                sp.GetRequiredService<GitProcess>(),
                askPlanner,
                submitReport,
                writeCheckpoint
            );
        });
        services.AddSingleton<DeliveryComposition>();
        return services;
    }
}
