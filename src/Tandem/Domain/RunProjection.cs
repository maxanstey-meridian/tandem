namespace Tandem.Domain;

public sealed record RunProjection(
    Guid RunId,
    string DurableRunId,
    string PacketPath,
    string RepositoryPath,
    RunStatus Status,
    string? ActiveBlockId,
    string? PinnedBaseSha,
    string? CandidateSha,
    string WorkspacePath,
    PendingHumanRequest? PendingHumanRequest,
    string? PublishedBranch,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt
)
{
    public static RunProjection Initial(
        Guid runId,
        string durableRunId,
        string packetPath,
        string repositoryPath,
        string workspacePath
    ) =>
        new(
            runId,
            durableRunId,
            packetPath,
            repositoryPath,
            RunStatus.Running,
            ActiveBlockId: null,
            PinnedBaseSha: null,
            CandidateSha: null,
            workspacePath,
            PendingHumanRequest: null,
            PublishedBranch: null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );
}

public sealed record PendingHumanRequest(string SourceBlockId, string Question, string Reason);
