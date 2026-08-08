using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tandem;

public static class TandemRegistration
{
    public static IServiceCollection AddTandem(this IServiceCollection services)
    {
        services.TryAddSingleton<AgentRuntime>();
        return services;
    }
}
