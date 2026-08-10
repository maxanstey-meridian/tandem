using System.Text.Json;
using Tandem.Delivery;
using Tandem.Ledger;
using Tandem.Terminal;

namespace Tandem.Tool;

internal sealed class DeliveryTerminalContributions(SqliteLedgerStore store, Guid runId)
{
    public async ValueTask<IReadOnlyList<TerminalPipelineEntry>> ReadAsync(
        CancellationToken cancellationToken
    )
    {
        var ledger = store.ForRun(runId);
        var delivery = new DeliveryLedger(ledger);
        var candidate = await delivery.ReadPublicationCandidateAsync(cancellationToken);
        var verification = await ledger.ReadAsync(
            DeliveryLedger.VerificationResults,
            cancellationToken
        );
        var publications = await ledger.ReadAsync(
            DeliveryLedger.PublicationResults,
            cancellationToken
        );
        var entries = new List<TerminalPipelineEntry>();
        if (candidate is not null)
        {
            entries.Add(new("candidate", Short(candidate.CandidateSha), ""));
        }
        foreach (var result in verification)
        {
            entries.Add(
                new(
                    "verify",
                    result.Value.Result.ExitCode == 0 ? "passed" : "failed",
                    result.Value.Result.Command,
                    result.Value.Result.Elapsed,
                    result.Value.Result.ExitCode == 0
                        ? TerminalPipelineEntryStyle.Success
                        : TerminalPipelineEntryStyle.Failure
                )
            );
        }
        if (publications.LastOrDefault() is { } publication)
        {
            entries.Add(
                new(
                    "published",
                    publication.Value.Branch,
                    "",
                    Style: TerminalPipelineEntryStyle.Success
                )
            );
        }
        return entries;
    }

    public static TerminalInteractionPrompt? FormatInteraction(
        PipelineInteractionRequestedObservation observation
    )
    {
        if (observation.Payload is not { } payload)
        {
            return null;
        }
        var question = payload.Deserialize<HumanQuestion>(JsonSerializerOptions.Web);
        return question is null ? null : new(question.Question, question.Reason);
    }

    private static string Short(string value) => value[..Math.Min(12, value.Length)];
}
