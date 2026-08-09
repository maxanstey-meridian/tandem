using System.Text.Json;
using Tandem.Ledger;

namespace Tandem.Tool;

internal sealed class RunInspector(SqliteLedgerStore store)
{
    public async ValueTask<RunInspection> InspectAsync(
        Guid runId,
        bool acceptedOnly,
        string? step,
        string? valueType,
        CancellationToken cancellationToken
    )
    {
        var run = await store.GetRunAsync(runId, cancellationToken);
        var entries = await store
            .ForRun(runId)
            .ReadAsync(PipelineJournal.Stream, cancellationToken);
        var items = entries
            .Select(entry =>
            {
                var accepted = PipelineJournal.IsAccepted(entry.Value);
                return new RunInspectionItem(
                    entry.RecordedAt,
                    accepted ? "accepted" : "runtime",
                    entry.Value.Kind.ToString(),
                    entry.Value.StepId,
                    entry.Value.Identity,
                    entry.Value.Name,
                    entry.Value.ValueType,
                    entry.Value.Result,
                    entry.Value.OutcomeKind,
                    entry.Value.Payload,
                    entry.Sequence
                );
            })
            .Where(item => !acceptedOnly || item.Category == "accepted")
            .Where(item => string.IsNullOrWhiteSpace(step) || item.StepId == step)
            .Where(item =>
                string.IsNullOrWhiteSpace(valueType)
                || item.ValueType?.Contains(valueType, StringComparison.OrdinalIgnoreCase) is true
            )
            .ToList();

        return new RunInspection(run.RunId, run.Composition, run.Status.ToString(), items);
    }
}

internal sealed record RunInspection(
    Guid RunId,
    string Composition,
    string Status,
    IReadOnlyList<RunInspectionItem> Items
)
{
    public int ContractVersion => 1;
}

internal sealed record RunInspectionItem(
    DateTimeOffset Timestamp,
    string Category,
    string Kind,
    string StepId,
    string? Identity,
    string? Name,
    string? ValueType,
    string? Result,
    string? OutcomeKind,
    JsonElement? Payload,
    long Sequence
);
