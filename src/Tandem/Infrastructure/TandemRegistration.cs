using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tandem.Application;
using Tandem.Domain;

namespace Tandem.Infrastructure;

public sealed record TandemEnvironment(string Home, string? ExecutablePath = null);

public interface ITandemChatClients
{
    public Microsoft.Extensions.AI.IChatClient Build(string profileName);
    public ResolvedProfile ResolveProfile(string profileName);
}

internal sealed class TandemChatClients(TandemConfig config) : ITandemChatClients
{
    private readonly ConcurrentDictionary<
        string,
        Lazy<Microsoft.Extensions.AI.IChatClient>
    > _clients = new(StringComparer.Ordinal);

    public Microsoft.Extensions.AI.IChatClient Build(string profileName)
    {
        return _clients
            .GetOrAdd(
                profileName,
                name => new Lazy<Microsoft.Extensions.AI.IChatClient>(
                    () =>
                    {
                        var profile = ResolveProfile(name);
                        var apiKey = EnvironmentApiKeyReader.Read(
                            config
                                .Providers[config.Profiles[name].Provider]
                                .ApiKeyEnvironmentVariable
                        );
                        return new ChatClientBuilder().Build(profile, apiKey);
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;
    }

    public ResolvedProfile ResolveProfile(string profileName)
    {
        if (!config.Profiles.TryGetValue(profileName, out var configured))
        {
            throw new ProfileResolutionException($"Profile '{profileName}' is not configured.");
        }
        if (!config.Providers.TryGetValue(configured.Provider, out var provider))
        {
            throw new ProfileResolutionException(
                $"Provider '{configured.Provider}' referenced by profile '{profileName}' is not configured."
            );
        }

        var apiKey = EnvironmentApiKeyReader.Read(provider.ApiKeyEnvironmentVariable);
        return new ProfileResolver().Resolve(config, profileName, apiKey);
    }
}

public static class TandemRegistration
{
    public static IServiceCollection AddTandem(this IServiceCollection services)
    {
        services.AddSingleton<ChatClientBuilder>();
        services.TryAddSingleton<ITandemChatClients, TandemChatClients>();
        services.TryAddSingleton(_ => new TandemEnvironment(TandemHomeResolver.Resolve()));
        services.TryAddSingleton(sp => new Lifecycle.LifecycleActionSetRegistry(
            sp.GetServices<Lifecycle.LifecycleActionSetRegistration>().ToArray()
        ));
        services.AddSingleton<RunSetup>();
        services.AddSingleton<WorkspacePreparation>();
        services.AddSingleton<GitProcess>();
        return services;
    }
}
