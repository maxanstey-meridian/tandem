using System.Text.Json;

namespace Tandem.Domain;

public sealed record SimpleV1State(
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
    RunStatus Status
)
{
    public static SimpleV1State Create(Packet packet, string pinnedBaseSha, string workspacePath) =>
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
            Status: RunStatus.Running
        );
}
