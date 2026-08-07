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
        services.AddSingleton<SupportStepsFactory>();
        services.AddSingleton<SupportComposition>();
        return services;
    }
}
