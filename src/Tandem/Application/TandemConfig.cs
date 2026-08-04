using Tandem.Domain;

namespace Tandem.Application;

public sealed record TandemConfig(
    IReadOnlyDictionary<string, ProviderConfig> Providers,
    IReadOnlyDictionary<string, ProfileConfig> Profiles
);

public sealed record ProviderConfig(
    string Type,
    string BaseUrl,
    string? ApiKeyEnvironmentVariable,
    WireApi WireApi
);

public sealed record ProfileConfig(
    string Provider,
    string Model,
    ReasoningLevel? ReasoningEffort,
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent
);
