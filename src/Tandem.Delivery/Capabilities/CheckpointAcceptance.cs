using Tandem.Git;

namespace Tandem.Delivery;

internal sealed class CheckpointAcceptance(GitProcess git, IDeliveryRecordSink records)
{
    public async ValueTask AcceptAsync(
        string acceptedCallId,
        DeliveryState state,
        WriteCheckpointRequest request,
        CancellationToken cancellationToken
    )
    {
        var changed = await ReadChangedFilesAsync(state, cancellationToken);
        var assessments = state.ReviewerDecision?.Outcomes.ToDictionary(
            outcome => outcome.OutcomeId,
            StringComparer.Ordinal
        );
        var outcomes = state
            .Packet.Outcomes.Select(outcome =>
            {
                var assessed = assessments?.GetValueOrDefault(outcome.Id);
                return new OutcomeProgress(
                    outcome.Id,
                    outcome.Description,
                    assessed?.Delivered ?? false,
                    assessed?.Evidence ?? []
                );
            })
            .ToArray();
        var constraints = state
            .Packet.Constraints.Concat(state.PlannerConstraints)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await records.AcceptCheckpointAsync(
            acceptedCallId,
            new ProgressCheckpointRecord(
                request.Summary,
                request.Completed,
                outcomes,
                changed,
                request.InspectedFiles,
                constraints,
                request.Uncertainties,
                request.NextAction
            ),
            cancellationToken
        );
    }

    private async ValueTask<IReadOnlyList<string>> ReadChangedFilesAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    )
    {
        var tracked = await git.RunAsync(
            state.WorkspacePath,
            ["diff", "--name-only", state.PinnedBaseSha],
            cancellationToken
        );
        EnsureSucceeded("git diff --name-only", tracked);
        var untracked = await git.RunAsync(
            state.WorkspacePath,
            ["ls-files", "--others", "--exclude-standard"],
            cancellationToken
        );
        EnsureSucceeded("git ls-files", untracked);
        return Lines(tracked.Stdout)
            .Concat(Lines(untracked.Stdout))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> Lines(string value) =>
        value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

    private static void EnsureSucceeded(string operation, GitResult result)
    {
        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException($"{operation} failed: {result.Stderr.Trim()}");
        }
    }
}
