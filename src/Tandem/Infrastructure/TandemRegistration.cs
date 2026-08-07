using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tandem.Actions;
using Tandem.Application;
using Tandem.Domain;
using Tandem.Git;
using Tandem.Infrastructure;

namespace Tandem;

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
        services.TryAddSingleton(sp =>
        {
            var environment = sp.GetRequiredService<TandemEnvironment>();
            return new AgentRuntime(environment.Home, environment.ExecutablePath);
        });
        services.TryAddSingleton(sp =>
        {
            var capabilities = services
                .Select(descriptor => descriptor.ImplementationInstance)
                .OfType<IAgentCapabilityRegistration>()
                .ToArray();
            var identityCollision = capabilities
                .GroupBy(capability => capability.Registration.Identity, StringComparer.Ordinal)
                .FirstOrDefault(group =>
                    group.Select(capability => capability.OwnerIdentity).Distinct().Count() > 1
                );
            if (identityCollision is not null)
            {
                throw new InvalidOperationException(
                    $"Lifecycle action set identity '{identityCollision.Key}' is shared by multiple state types."
                );
            }
            var duplicate = capabilities
                .GroupBy(capability => (capability.Registration.Identity, capability.ToolName))
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException(
                    $"Capability '{duplicate.Key.ToolName}' is registered more than once for action set '{duplicate.Key.Identity}'."
                );
            }
            return new LifecycleActionSetRegistry([
                .. sp.GetServices<LifecycleActionSetRegistration>(),
                .. capabilities.Select(capability => capability.Registration),
            ]);
        });
        services.AddSingleton<RunSetup>();
        services.AddSingleton<GitProcess>();
        return services;
    }
}
