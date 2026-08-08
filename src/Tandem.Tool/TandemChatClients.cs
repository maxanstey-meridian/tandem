using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Tandem.Application;
using Tandem.Domain;
using Tandem.Infrastructure;

namespace Tandem.Tool;

internal sealed class TandemChatClients(TandemConfig config)
{
    private readonly ConcurrentDictionary<string, Lazy<IChatClient>> _clients = new(
        StringComparer.Ordinal
    );

    public IChatClient Build(string profileName) =>
        _clients
            .GetOrAdd(
                profileName,
                name => new Lazy<IChatClient>(
                    () =>
                    {
                        var profile = ResolveProfile(name);
                        var apiKey = EnvironmentApiKeyReader.Read(
                            config
                                .Providers[config.Profiles[name].Provider]
                                .ApiKeyEnvironmentVariable
                        );
                        return new Tandem.Infrastructure.ChatClientBuilder().Build(profile, apiKey);
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;

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
