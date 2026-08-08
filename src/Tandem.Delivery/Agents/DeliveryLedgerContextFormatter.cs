using System.Text;

namespace Tandem.Delivery;

internal static class DeliveryLedgerContextFormatter
{
    private const int CharacterBudget = 8_000;

    public static string Format(DeliveryLedgerContext context)
    {
        var text = new StringBuilder("<durable-delivery-context>\n");
        Append(
            text,
            "Outcomes",
            context.Outcomes?.Outcomes.Select(outcome =>
                $"[{outcome.Id}] delivered={outcome.Delivered}; evidence={string.Join("; ", outcome.Evidence)}"
            )
        );
        if (context.LatestCheckpoint is { } checkpoint)
        {
            Append(
                text,
                "Latest checkpoint",
                [
                    $"Summary: {checkpoint.Summary}",
                    $"Changed files: {string.Join("; ", checkpoint.ChangedFiles)}",
                    $"Next action: {checkpoint.NextAction}",
                ]
            );
        }
        if (context.Report is { } report)
        {
            Append(
                text,
                "Accepted report",
                [report.Summary, $"Evidence: {string.Join("; ", report.Evidence)}"]
            );
        }
        Append(
            text,
            "Planner decisions",
            context.PlannerDecisions.Select(decision =>
                $"{decision.Decision}: {decision.Rationale}; constraints={string.Join("; ", decision.Constraints)}"
            )
        );
        Append(
            text,
            "Verification",
            context.VerificationResults.Select(result =>
                $"{result.Command}: exit={result.ExitCode}; stderr={result.Stderr}"
            )
        );
        Append(
            text,
            "Reviews",
            context.Reviews.Select(decision => $"{decision.Decision}: {decision.Summary}")
        );
        Append(
            text,
            "Human answers",
            context.HumanAnswers.Select(record => $"{record.InteractionId}: {record.Answer.Text}")
        );
        text.Append("</durable-delivery-context>");
        if (text.Length <= CharacterBudget)
        {
            return text.ToString();
        }
        const string marker = "\n[durable context truncated]\n</durable-delivery-context>";
        return text.ToString(0, CharacterBudget - marker.Length) + marker;
    }

    private static void Append(StringBuilder text, string heading, IEnumerable<string>? values)
    {
        var materialized = values?.ToArray() ?? [];
        if (materialized.Length == 0)
        {
            return;
        }
        text.AppendLine($"{heading}:");
        foreach (var value in materialized)
        {
            text.AppendLine($"- {value}");
        }
    }
}
