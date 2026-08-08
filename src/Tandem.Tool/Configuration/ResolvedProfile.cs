namespace Tandem.Domain;

public sealed record ResolvedProfile(
    string ProviderName,
    string BaseUrl,
    string Model,
    WireApi WireApi,
    ReasoningLevel? Reasoning,
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent
);

public enum WireApi
{
    Completions,
    Responses,
}

public enum ReasoningLevel
{
    Low,
    Medium,
    High,
}
