using Microsoft.Extensions.DependencyInjection;
using Tandem.Infrastructure.Lifecycle;

namespace Tandem.Infrastructure.Composition;

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
        services.AddSingleton<DeliveryStepsFactory>(sp =>
        {
            var clients = sp.GetRequiredService<ITandemChatClients>();
            var environment = sp.GetRequiredService<TandemEnvironment>();
            return new DeliveryStepsFactory(
                environment.Home,
                clients.Build,
                clients.ResolveProfile,
                environment.ExecutablePath
            );
        });
        services.AddSingleton<DeliveryComposition>();
        return services;
    }
}
