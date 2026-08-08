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
    IReadOnlyDictionary<string, AgentProfileSelection> AgentProfiles,
    HashSet<string> GateLatches
)
{
    public static PipelineRuntime Create(Guid runId) =>
        new(
            runId,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, AgentUsage>(),
            new Dictionary<string, int>(),
            new Dictionary<string, AgentProfileSelection>(),
            new HashSet<string>(StringComparer.Ordinal)
        );

    public string NextInvocationId(string stepId) =>
        $"{RunId:N}--{stepId}--{InvocationCounts.GetValueOrDefault(stepId) + 1}";

    public PipelineRuntime IncrementInvocations(string stepId) =>
        this with
        {
            InvocationCounts = new Dictionary<string, int>(InvocationCounts)
            {
                [stepId] = InvocationCounts.GetValueOrDefault(stepId) + 1,
            },
        };

    public PipelineRuntime WithSession(string stepId, JsonElement session) =>
        this with
        {
            AgentSessions = new Dictionary<string, JsonElement>(AgentSessions)
            {
                [stepId] = session,
            },
        };

    public PipelineRuntime WithoutSession(string stepId) =>
        this with
        {
            AgentSessions = AgentSessions
                .Where(kvp => kvp.Key != stepId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public PipelineRuntime WithUsage(string stepId, AgentUsage usage) =>
        this with
        {
            AgentUsage = new Dictionary<string, AgentUsage>(AgentUsage) { [stepId] = usage },
        };

    public PipelineRuntime WithoutUsage(string stepId) =>
        this with
        {
            AgentUsage = AgentUsage
                .Where(kvp => kvp.Key != stepId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public PipelineRuntime WithProfile(string stepId, AgentProfileSelection decision) =>
        this with
        {
            AgentProfiles = new Dictionary<string, AgentProfileSelection>(AgentProfiles)
            {
                [stepId] = decision,
            },
        };

    public PipelineRuntime WithoutProfile(string stepId) =>
        this with
        {
            AgentProfiles = AgentProfiles
                .Where(kvp => kvp.Key != stepId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public bool IsGateLatched(string stepId, string gateId) =>
        GateLatches.Contains($"{stepId}:{gateId}");

    public PipelineRuntime WithGateLatch(string stepId, string gateId) =>
        this with
        {
            GateLatches = new HashSet<string>(GateLatches, StringComparer.Ordinal)
            {
                $"{stepId}:{gateId}",
            },
        };

    public PipelineRuntime WithoutGateLatch(string stepId, string gateId) =>
        this with
        {
            GateLatches = GateLatches
                .Where(key => !string.Equals(key, $"{stepId}:{gateId}", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal),
        };
}

internal sealed record BlockOutcome(
    string Kind,
    string StepId,
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
    TimeSpan LastModelCallDuration,
    long CumulativeInputTokens = 0,
    long CumulativeOutputTokens = 0
);
