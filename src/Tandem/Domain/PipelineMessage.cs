using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem.Domain;

public sealed record PipelineMessage(PipelineContext Context, BlockOutcome? LatestOutcome = null)
{
    public PipelineMessage WithOutcome(BlockOutcome outcome) =>
        this with
        {
            LatestOutcome = outcome,
        };
}

public sealed record PipelineContext(
    Guid RunId,
    Packet Packet,
    string PinnedBaseSha,
    string WorkspacePath,
    bool MutationAuthorized,
    PlannerDecision? PlannerDecision,
    IReadOnlyList<string> PlannerConstraints,
    string? CandidateSha,
    int VerificationIndex,
    IReadOnlyList<VerificationResult> VerificationResults,
    IReadOnlyDictionary<string, JsonElement> AgentSessions,
    IReadOnlyDictionary<string, AgentUsage> AgentUsage,
    IReadOnlyDictionary<string, int> InvocationCounts,
    JsonElement? CheckpointPayload,
    JsonElement? ImplementationReport,
    RunStatus Status
)
{
    public static PipelineContext Create(
        Guid runId,
        Packet packet,
        string pinnedBaseSha,
        string workspacePath
    ) =>
        new(
            runId,
            packet,
            pinnedBaseSha,
            workspacePath,
            MutationAuthorized: false,
            PlannerDecision: null,
            PlannerConstraints: [],
            CandidateSha: null,
            VerificationIndex: 0,
            VerificationResults: [],
            AgentSessions: new Dictionary<string, JsonElement>(),
            AgentUsage: new Dictionary<string, AgentUsage>(),
            InvocationCounts: new Dictionary<string, int>(),
            CheckpointPayload: null,
            ImplementationReport: null,
            Status: RunStatus.Running
        );

    public string NextInvocationId(string blockId)
    {
        var counts = new Dictionary<string, int>(InvocationCounts);
        counts[blockId] = counts.GetValueOrDefault(blockId) + 1;
        return $"{RunId:N}--{blockId}--{counts[blockId]}";
    }

    public PipelineContext IncrementInvocations(string blockId) =>
        this with
        {
            InvocationCounts = new Dictionary<string, int>(InvocationCounts)
            {
                [blockId] = InvocationCounts.GetValueOrDefault(blockId) + 1,
            },
        };

    public PipelineContext WithSession(string blockId, JsonElement session) =>
        this with
        {
            AgentSessions = new Dictionary<string, JsonElement>(AgentSessions)
            {
                [blockId] = session,
            },
        };

    public PipelineContext WithoutSession(string blockId) =>
        this with
        {
            AgentSessions = AgentSessions
                .Where(kvp => kvp.Key != blockId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public PipelineContext WithUsage(string blockId, AgentUsage usage) =>
        this with
        {
            AgentUsage = new Dictionary<string, AgentUsage>(AgentUsage) { [blockId] = usage },
        };

    public PipelineContext WithCheckpoint(JsonElement? checkpointPayload) =>
        this with
        {
            CheckpointPayload = checkpointPayload,
        };

    public PipelineContext WithImplementationReport(JsonElement? report) =>
        this with
        {
            ImplementationReport = report,
        };
}

public sealed record BlockOutcome(
    string Kind,
    string BlockId,
    string Summary,
    JsonElement Payload = default
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
