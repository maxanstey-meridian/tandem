using System.Text.Json;

namespace Tandem;

public abstract record AgentUpdate
{
    private AgentUpdate() { }

    public sealed record Text(string Value) : AgentUpdate;

    public sealed record Reasoning(string Value) : AgentUpdate;

    public sealed record Usage(long? InputTokens, long? OutputTokens, long? ReasoningTokens)
        : AgentUpdate;

    public sealed record ToolStarted(string CallId, string Name, JsonElement Arguments)
        : AgentUpdate;

    public sealed record ToolCompleted(string CallId, string? Result, string? Error) : AgentUpdate
    {
        public bool Succeeded => Error is null;
    }
}
