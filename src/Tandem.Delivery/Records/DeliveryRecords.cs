namespace Tandem.Delivery;

public sealed record OutcomeProgress(
    string Id,
    string Description,
    bool Delivered,
    IReadOnlyList<string> Evidence
);

public sealed record OutcomeProgressDocument(
    string AcceptedDecisionId,
    IReadOnlyList<OutcomeProgress> Outcomes
);

public sealed record ProgressCheckpointRecord(
    string Summary,
    IReadOnlyList<string> Completed,
    IReadOnlyList<OutcomeProgress> Outcomes,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> InspectedFiles,
    IReadOnlyList<string> AcceptedConstraints,
    IReadOnlyList<string> Uncertainties,
    string NextAction
);

public sealed record PublicationCandidateDocument(
    string AcceptedCandidateId,
    string Repository,
    string WorkspacePath,
    string PacketTitle,
    string PinnedBaseSha,
    string CandidateSha
);

public sealed record PublicationResultRecord(
    string Repository,
    string Branch,
    string CandidateSha,
    bool Reconciled
);

public sealed record HumanAnswerRecord(
    string RequestId,
    string InteractionId,
    HumanQuestion Question,
    HumanAnswer Answer
);

public sealed record VerificationResultRecord(VerificationResult Result);
