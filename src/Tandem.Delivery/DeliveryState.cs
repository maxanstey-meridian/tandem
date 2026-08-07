using System.Text.Json;
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
    string? ReviewerHumanAnswer,
    string? HumanAnswerSourceBlockId,
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
            ReviewerHumanAnswer: null,
            HumanAnswerSourceBlockId: null,
            Status: RunStatus.Running
        );
}
