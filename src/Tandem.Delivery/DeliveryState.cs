using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Domain;

namespace Tandem.Delivery;

public sealed record DeliveryState(
    Packet Packet,
    string PinnedBaseSha,
    string WorkspacePath,
    bool MutationAuthorized,
    PlannerDecision? PlannerDecision,
    IReadOnlyList<string> PlannerConstraints,
    string? CandidateSha,
    int VerificationIndex,
    IReadOnlyList<VerificationResult> VerificationResults,
    JsonElement? CheckpointPayload,
    JsonElement? ImplementationReport,
    ReviewDecision? ReviewerDecision,
    string? ReviewerHumanAnswer,
    string? HumanAnswerSourceBlockId,
    ExecutorAction? LastExecutorAction,
    RunStatus Status
)
{
    public static DeliveryState Create(Packet packet, string pinnedBaseSha, string workspacePath) =>
        new(
            packet,
            pinnedBaseSha,
            workspacePath,
            MutationAuthorized: false,
            PlannerDecision: null,
            PlannerConstraints: [],
            CandidateSha: null,
            VerificationIndex: 0,
            VerificationResults: [],
            CheckpointPayload: null,
            ImplementationReport: null,
            ReviewerDecision: null,
            ReviewerHumanAnswer: null,
            HumanAnswerSourceBlockId: null,
            LastExecutorAction: null,
            Status: RunStatus.Running
        );
}

public enum ExecutorAction
{
    PlannerRequested,
    ReportSubmitted,
    CheckpointWritten,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Running,
    Ready,
    WaitingForHuman,
    Failed,
    Faulted,
    Cancelled,
}
