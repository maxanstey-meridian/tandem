using System.Text.Json;
using Tandem.Application;
using Tandem.Domain;

namespace Tandem.Infrastructure;

public sealed class ConfigurationLoadException : Exception
{
    public ConfigurationLoadException(string message)
        : base(message) { }

    public ConfigurationLoadException(string message, Exception inner)
        : base(message, inner) { }
}

public sealed class TandemConfigurationLoader
{
    public TandemConfig Load(string tandemHome)
    {
        var configPath = Path.Combine(tandemHome, "config.json");
        if (!File.Exists(configPath))
        {
            throw new ConfigurationLoadException($"Tandem configuration not found: {configPath}");
        }

        var raw = File.ReadAllText(configPath);
        ConfigJson doc;
        try
        {
            doc =
                JsonSerializer.Deserialize<ConfigJson>(raw, _jsonOptions)
                ?? throw new ConfigurationLoadException($"Configuration is empty: {configPath}");
        }
        catch (JsonException ex)
        {
            throw new ConfigurationLoadException(
                $"Configuration is not valid JSON: {configPath}",
                ex
            );
        }

        var providers = (doc.Providers ?? new Dictionary<string, ProviderJson>()).ToDictionary(
            p => p.Key,
            p => ValidateProvider(p.Key, p.Value),
            StringComparer.Ordinal
        );
        var profiles = (doc.Profiles ?? new Dictionary<string, ProfileJson>()).ToDictionary(
            p => p.Key,
            p => ValidateProfile(p.Key, p.Value, providers),
            StringComparer.Ordinal
        );

        return new TandemConfig(providers, profiles);
    }

    private static ProviderConfig ValidateProvider(string name, ProviderJson p)
    {
        if (p.Type is null || p.Type != "openai")
        {
            throw new ConfigurationLoadException(
                $"Provider '{name}' has unsupported type; only 'openai' is supported."
            );
        }

        if (
            string.IsNullOrWhiteSpace(p.BaseUrl)
            || !Uri.IsWellFormedUriString(p.BaseUrl, UriKind.Absolute)
        )
        {
            throw new ConfigurationLoadException(
                $"Provider '{name}' baseUrl must be an absolute URL."
            );
        }

        var wire = p.WireApi switch
        {
            "completions" => Domain.WireApi.Completions,
            "responses" => Domain.WireApi.Responses,
            _ => throw new ConfigurationLoadException(
                $"Provider '{name}' wireApi must be 'completions' or 'responses'."
            ),
        };

        return new ProviderConfig(p.Type, p.BaseUrl, p.ApiKeyEnvironmentVariable, wire);
    }

    private static ProfileConfig ValidateProfile(
        string name,
        ProfileJson p,
        IReadOnlyDictionary<string, ProviderConfig> providers
    )
    {
        if (
            string.IsNullOrWhiteSpace(p.Provider)
            || !providers.TryGetValue(p.Provider, out var provider)
        )
        {
            throw new ConfigurationLoadException(
                $"Profile '{name}' references unknown provider '{p.Provider}'."
            );
        }

        if (string.IsNullOrWhiteSpace(p.Model))
        {
            throw new ConfigurationLoadException($"Profile '{name}' model is required.");
        }

        if (p.ContextWindowTokens <= 0)
        {
            throw new ConfigurationLoadException(
                $"Profile '{name}' contextWindowTokens must be positive."
            );
        }

        if (p.MaxOutputTokens <= 0)
        {
            throw new ConfigurationLoadException(
                $"Profile '{name}' maxOutputTokens must be positive."
            );
        }

        if (p.MaxOutputTokens >= p.ContextWindowTokens)
        {
            throw new ConfigurationLoadException(
                $"Profile '{name}' maxOutputTokens must be below contextWindowTokens."
            );
        }

        if (p.CheckpointAtPercent < 50 || p.CheckpointAtPercent > 95)
        {
            throw new ConfigurationLoadException(
                $"Profile '{name}' checkpointAtPercent must be between 50 and 95."
            );
        }

        ReasoningLevel? effort = null;
        if (p.ReasoningEffort is not null)
        {
            effort = p.ReasoningEffort switch
            {
                "low" => Domain.ReasoningLevel.Low,
                "medium" => Domain.ReasoningLevel.Medium,
                "high" => Domain.ReasoningLevel.High,
                _ => throw new ConfigurationLoadException(
                    $"Profile '{name}' reasoningEffort must be 'low', 'medium', or 'high'."
                ),
            };
        }

        return new ProfileConfig(
            p.Provider,
            p.Model,
            effort,
            p.ContextWindowTokens,
            p.MaxOutputTokens,
            p.CheckpointAtPercent
        );
    }

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ConfigJson(
        IReadOnlyDictionary<string, ProviderJson>? Providers,
        IReadOnlyDictionary<string, ProfileJson>? Profiles
    );

    private sealed record ProviderJson(
        string? Type,
        string? BaseUrl,
        string? ApiKeyEnvironmentVariable,
        string? WireApi
    );

    private sealed record ProfileJson(
        string? Provider,
        string? Model,
        string? ReasoningEffort,
        int ContextWindowTokens,
        int MaxOutputTokens,
        int CheckpointAtPercent
    );
}
