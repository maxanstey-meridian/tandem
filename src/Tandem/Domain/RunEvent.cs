using System.Text.Json;

namespace Tandem.Domain;

public sealed record RunEvent(
    string EventId,
    DateTimeOffset Timestamp,
    Guid RunId,
    string BlockId,
    string Kind,
    string Message,
    JsonElement? Data
)
{
    public static RunEvent Create(
        string blockId,
        string kind,
        string message,
        JsonElement? data = null,
        string? eventIdSuffix = null
    )
    {
        var runId = Guid.Empty;
        var ts = DateTimeOffset.UtcNow;
        var suffix = eventIdSuffix is not null ? $"--{eventIdSuffix}" : "";
        return new RunEvent(
            $"{ts.ToUnixTimeMilliseconds()}-{blockId}-{kind}{suffix}",
            ts,
            runId,
            blockId,
            kind,
            message,
            data
        );
    }
}

public static class EventKinds
{
    public const string RunStarted = "run.started";
    public const string RunResumed = "run.resumed";
    public const string RunReady = "run.ready";
    public const string RunFailed = "run.failed";
    public const string RunPublished = "run.published";
    public const string BlockStarted = "block.started";
    public const string BlockCompleted = "block.completed";
    public const string AgentReasoning = "agent.reasoning";
    public const string AgentText = "agent.text";
    public const string AgentUsage = "agent.usage";
    public const string ToolStarted = "tool.started";
    public const string ToolCompleted = "tool.completed";
    public const string CommandOutput = "command.output";
    public const string HumanRequested = "human.requested";
    public const string HumanAnswered = "human.answered";
}
