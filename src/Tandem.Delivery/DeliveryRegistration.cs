using Microsoft.Extensions.DependencyInjection;
using Tandem.Actions;
using Tandem.Git;

namespace Tandem.Delivery;

public static class DeliveryRegistration
{
    public static IServiceCollection AddDelivery(this IServiceCollection services)
    {
        services.AddSingleton(
            new LifecycleActionSetRegistration(
                DeliveryLifecycleActions.Identity,
                DeliveryLifecycleActions.Register
            )
        );
        services.AddSingleton<WorkspacePreparation>();
        services.AddSingleton<DeliveryDiffAcquisition>();
        services.AddSingleton<DeliveryStepsFactory>(sp =>
        {
            var clients = sp.GetRequiredService<ITandemChatClients>();
            return new DeliveryStepsFactory(
                sp.GetRequiredService<AgentRuntime>(),
                clients.Build,
                clients.ResolveProfile,
                sp.GetRequiredService<DeliveryDiffAcquisition>(),
                sp.GetRequiredService<WorkspacePreparation>(),
                sp.GetRequiredService<GitProcess>()
            );
        });
        services.AddSingleton<DeliveryComposition>();
        return services;
    }
}
