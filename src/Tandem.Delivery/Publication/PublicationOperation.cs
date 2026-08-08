using System.Text;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed class PublicationOperation(GitProcess git, IDeliveryRecordSink records)
{
    public async ValueTask<PublicationResultRecord> ExecuteAsync(
        string? explicitBranch,
        CancellationToken cancellationToken
    )
    {
        var candidate =
            await records.ReadPublicationCandidateAsync(cancellationToken)
            ?? throw new InvalidOperationException("Run has no accepted publication candidate.");
        var branch = string.IsNullOrWhiteSpace(explicitBranch)
            ? $"tandem/{Slugify(candidate.PacketTitle)}-{candidate.CandidateSha[..8]}"
            : explicitBranch;
        await RequireSuccessAsync(
            null,
            ["check-ref-format", "--branch", branch],
            $"Invalid branch name '{branch}'",
            cancellationToken
        );
        var head = await RequireSuccessAsync(
            candidate.WorkspacePath,
            ["rev-parse", "HEAD"],
            "Could not read workspace HEAD",
            cancellationToken
        );
        if (!string.Equals(head, candidate.CandidateSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Workspace HEAD '{head}' does not equal candidate '{candidate.CandidateSha}'."
            );
        }
        await RequireSuccessAsync(
            candidate.Repository,
            ["cat-file", "-e", candidate.PinnedBaseSha],
            $"Pinned base '{candidate.PinnedBaseSha}' is not available",
            cancellationToken
        );

        var existing = await ReadBranchAsync(candidate.Repository, branch, cancellationToken);
        if (existing is not null)
        {
            return await ReconcileAsync(candidate, branch, existing, cancellationToken);
        }

        var push = await git.RunAsync(
            candidate.WorkspacePath,
            ["push", candidate.Repository, $"{candidate.CandidateSha}:refs/heads/{branch}"],
            cancellationToken
        );
        if (push.ExitCode != 0 || push.TimedOut)
        {
            var afterFailure = await ReadBranchAsync(
                candidate.Repository,
                branch,
                cancellationToken
            );
            if (afterFailure is null)
            {
                throw new InvalidOperationException($"git push failed: {push.Stderr.Trim()}");
            }
            return await ReconcileAsync(candidate, branch, afterFailure, cancellationToken);
        }

        var published =
            await ReadBranchAsync(candidate.Repository, branch, cancellationToken)
            ?? throw new InvalidOperationException("Published branch could not be resolved.");
        return await ReconcileAsync(candidate, branch, published, cancellationToken);
    }

    private async ValueTask<PublicationResultRecord> ReconcileAsync(
        PublicationCandidateDocument candidate,
        string branch,
        string publishedSha,
        CancellationToken cancellationToken
    )
    {
        if (
            !string.Equals(publishedSha, candidate.CandidateSha, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                $"Branch '{branch}' resolves to '{publishedSha}', not candidate '{candidate.CandidateSha}'."
            );
        }
        var result = new PublicationResultRecord(
            candidate.Repository,
            branch,
            candidate.CandidateSha,
            Reconciled: true
        );
        await records.AcceptPublicationResultAsync(result, cancellationToken);
        return result;
    }

    private async ValueTask<string?> ReadBranchAsync(
        string repository,
        string branch,
        CancellationToken cancellationToken
    )
    {
        var result = await git.RunAsync(
            repository,
            ["rev-parse", "--verify", $"refs/heads/{branch}"],
            cancellationToken
        );
        return result.ExitCode == 0 && !result.TimedOut ? result.Stdout.Trim() : null;
    }

    private async ValueTask<string> RequireSuccessAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        string message,
        CancellationToken cancellationToken
    )
    {
        var result = await git.RunAsync(workingDirectory, arguments, cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException($"{message}: {result.Stderr.Trim()}");
        }
        return result.Stdout.Trim();
    }

    private static string Slugify(string input)
    {
        var slug = new StringBuilder();
        var previousDash = false;
        foreach (var character in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                slug.Append(character);
                previousDash = false;
            }
            else if (!previousDash && slug.Length > 0)
            {
                slug.Append('-');
                previousDash = true;
            }
        }
        return slug.ToString().Trim('-');
    }
}
