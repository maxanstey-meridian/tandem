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
        services.AddSingleton<SongwriterStepsFactory>();
        services.AddSingleton<SongwriterComposition>();
        return services;
    }
}
