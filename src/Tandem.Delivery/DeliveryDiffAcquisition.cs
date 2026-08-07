using Tandem.Git;

namespace Tandem.Delivery;

public sealed class DeliveryDiffAcquisition(GitProcess git)
{
    public async ValueTask<string> AcquireAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    )
    {
        var range = $"{state.PinnedBaseSha}..{state.CandidateSha}";
        var nameStatusResult = await git.RunAsync(
            state.WorkspacePath,
            ["diff", "--name-status", "-z", range],
            cancellationToken
        );
        var diffResult = await git.RunAsync(
            state.WorkspacePath,
            ["diff", "--binary", range],
            cancellationToken
        );
        var changedFiles = nameStatusResult.Stdout.Replace('\0', '\n');

        return $"""
            Changed files:
            {changedFiles}

            Diff:
            {diffResult.Stdout}
            """;
    }
}
