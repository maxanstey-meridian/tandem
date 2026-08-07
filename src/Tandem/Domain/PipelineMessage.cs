using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem.Domain;

public interface IOutcomeBearingMessage
{
    public BlockOutcome? LatestOutcome { get; }
}

public sealed record PipelineMessage<TState>(
    PipelineRuntime Runtime,
    TState State,
    BlockOutcome? LatestOutcome = null,
    PipelineResult? LatestResult = null,
    PipelineRunDisposition? Disposition = null
) : IOutcomeBearingMessage
{
    public PipelineMessage<TState> WithOutcome(BlockOutcome outcome) =>
        this with
        {
            LatestOutcome = outcome,
        };
}

public sealed record PipelineResult(string StepId, string CaseId, JsonElement Payload);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineRunDisposition
{
    Failed,
}

public sealed record PipelineRuntime(
    Guid RunId,
    IReadOnlyDictionary<string, JsonElement> AgentSessions,
    IReadOnlyDictionary<string, AgentUsage> AgentUsage,
    IReadOnlyDictionary<string, int> InvocationCounts,
    IReadOnlyDictionary<string, AgentProfileDecision> AgentProfiles
)
{
    public static PipelineRuntime Create(Guid runId) =>
        new(
            runId,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, AgentUsage>(),
            new Dictionary<string, int>(),
            new Dictionary<string, AgentProfileDecision>()
        );

    public string NextInvocationId(string blockId) =>
        $"{RunId:N}--{blockId}--{InvocationCounts.GetValueOrDefault(blockId) + 1}";

    public PipelineRuntime IncrementInvocations(string blockId) =>
        this with
        {
            InvocationCounts = new Dictionary<string, int>(InvocationCounts)
            {
                [blockId] = InvocationCounts.GetValueOrDefault(blockId) + 1,
            },
        };

    public PipelineRuntime WithSession(string blockId, JsonElement session) =>
        this with
        {
            AgentSessions = new Dictionary<string, JsonElement>(AgentSessions)
            {
                [blockId] = session,
            },
        };

    public PipelineRuntime WithoutSession(string blockId) =>
        this with
        {
            AgentSessions = AgentSessions
                .Where(kvp => kvp.Key != blockId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public PipelineRuntime WithUsage(string blockId, AgentUsage usage) =>
        this with
        {
            AgentUsage = new Dictionary<string, AgentUsage>(AgentUsage) { [blockId] = usage },
        };

    public PipelineRuntime WithoutUsage(string blockId) =>
        this with
        {
            AgentUsage = AgentUsage
                .Where(kvp => kvp.Key != blockId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public PipelineRuntime WithProfile(string blockId, AgentProfileDecision decision) =>
        this with
        {
            AgentProfiles = new Dictionary<string, AgentProfileDecision>(AgentProfiles)
            {
                [blockId] = decision,
            },
        };

    public PipelineRuntime WithoutProfile(string blockId) =>
        this with
        {
            AgentProfiles = AgentProfiles
                .Where(kvp => kvp.Key != blockId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };
}

public sealed record BlockOutcome(
    string Kind,
    string BlockId,
    string Summary,
    JsonElement Payload = default,
    TimeSpan Duration = default
);

public sealed record AgentUsage(
    int CurrentInputTokens,
    int CurrentOutputTokens,
    int CurrentContextTokens,
    int ContextWindowTokens,
    int CheckpointAtTokens,
    TimeSpan LastModelCallDuration
);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Running,
    Ready,
    WaitingForHuman,
    Failed,
}
