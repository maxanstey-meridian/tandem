using System.Text.Json;

namespace Tandem.Domain;

internal interface IOutcomeBearingMessage
{
    public BlockOutcome? LatestOutcome { get; }
}

internal sealed record PipelineMessage<TState>(
    PipelineRuntime Runtime,
    TState State,
    BlockOutcome? LatestOutcome = null,
    PipelineResult? LatestResult = null,
    PipelineRunStatus Status = PipelineRunStatus.Succeeded
) : IOutcomeBearingMessage, IPipelineRunContextCarrier
{
    internal PipelineRunContext? RunContext { get; init; }
    PipelineRunContext? IPipelineRunContextCarrier.RunContext => RunContext;

    public PipelineMessage<TState> WithOutcome(BlockOutcome outcome) =>
        this with
        {
            LatestOutcome = outcome,
        };
}

internal sealed record PipelineResult(string StepId, string CaseId, JsonElement Payload);

internal sealed record PipelineRuntime(
    Guid RunId,
    IReadOnlyDictionary<string, JsonElement> AgentSessions,
    IReadOnlyDictionary<string, AgentUsage> AgentUsage,
    IReadOnlyDictionary<string, int> InvocationCounts,
    IReadOnlyDictionary<string, AgentProfileSelection> AgentProfiles
)
{
    public static PipelineRuntime Create(Guid runId) =>
        new(
            runId,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, AgentUsage>(),
            new Dictionary<string, int>(),
            new Dictionary<string, AgentProfileSelection>()
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

    public PipelineRuntime WithProfile(string blockId, AgentProfileSelection decision) =>
        this with
        {
            AgentProfiles = new Dictionary<string, AgentProfileSelection>(AgentProfiles)
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

internal sealed record BlockOutcome(
    string Kind,
    string BlockId,
    string Summary,
    JsonElement Payload = default,
    TimeSpan Duration = default
);

internal sealed record AgentUsage(
    int CurrentInputTokens,
    int CurrentOutputTokens,
    int CurrentContextTokens,
    int ContextWindowTokens,
    int CheckpointAtTokens,
    TimeSpan LastModelCallDuration
);
