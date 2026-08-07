using Microsoft.Extensions.DependencyInjection;

namespace Tandem.Sample.Songwriter;

public static class SongwriterRegistration
{
    public static IServiceCollection AddSongwriter(
        this IServiceCollection services,
        SongwriterClients clients
    )
    {
        services.AddSingleton(clients);
        services.AddSingleton(sp =>
            SongwriterDefinitions.Create(
                sp.GetRequiredService<AgentRuntime>(),
                sp.GetRequiredService<SongwriterClients>()
            )
        );
        services.AddSingleton<SongwriterComposition>();
        return services;
    }
}
