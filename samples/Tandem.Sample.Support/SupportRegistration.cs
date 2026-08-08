using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Tandem.Sample.Support;

public sealed record SupportOptions(IChatClient ClassifierClient, IChatClient ResolverClient);

public static class SupportRegistration
{
    public static IServiceCollection AddCustomerSupport(
        this IServiceCollection services,
        SupportOptions options
    )
    {
        services.AddSingleton(options);
        services.AddSingleton(sp =>
            SupportDefinitions.Create(
                sp.GetRequiredService<AgentFactory>(),
                sp.GetRequiredService<SupportOptions>(),
                sp.GetRequiredService<IAccountLookup>()
            )
        );
        services.AddSingleton<SupportComposition>();
        return services;
    }
}
