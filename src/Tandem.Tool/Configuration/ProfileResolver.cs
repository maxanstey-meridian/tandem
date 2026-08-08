using Tandem.Domain;

namespace Tandem.Application;

public sealed class ProfileResolver
{
    public ResolvedProfile Resolve(TandemConfig config, string profileName, string apiKey)
    {
        if (!config.Profiles.TryGetValue(profileName, out var profile))
        {
            throw new ProfileResolutionException($"Profile '{profileName}' is not configured.");
        }

        if (!config.Providers.TryGetValue(profile.Provider, out var provider))
        {
            throw new ProfileResolutionException(
                $"Provider '{profile.Provider}' referenced by profile '{profileName}' is not configured."
            );
        }

        if (provider.ApiKeyEnvironmentVariable is not null && string.IsNullOrEmpty(apiKey))
        {
            throw new ProfileResolutionException(
                $"Provider '{profile.Provider}' requires an API key from environment variable '{provider.ApiKeyEnvironmentVariable}', but it is not set."
            );
        }

        return new ResolvedProfile(
            profile.Provider,
            provider.BaseUrl,
            profile.Model,
            provider.WireApi,
            profile.ReasoningEffort,
            profile.ContextWindowTokens,
            profile.MaxOutputTokens,
            profile.CheckpointAtPercent
        );
    }
}

public sealed class ProfileResolutionException(string message) : Exception(message);
