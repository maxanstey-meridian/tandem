namespace Tandem.Delivery;

public sealed record RunProjection(
    Guid RunId,
    string CompositionIdentity,
    string PacketPath,
    string RepositoryPath,
    RunStatus Status,
    string? ActiveBlockId,
    string? PinnedBaseSha,
    string? CandidateSha,
    string WorkspacePath,
    string? PublishedBranch,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt
)
{
    public static RunProjection Initial(
        Guid runId,
        string compositionIdentity,
        string packetPath,
        string repositoryPath,
        string workspacePath
    ) =>
        new(
            runId,
            compositionIdentity,
            packetPath,
            repositoryPath,
            RunStatus.Running,
            ActiveBlockId: null,
            PinnedBaseSha: null,
            CandidateSha: null,
            workspacePath,
            PublishedBranch: null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );
}
