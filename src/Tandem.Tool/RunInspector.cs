using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Ledger;

namespace Tandem.Tool;

internal sealed class RunInspector(SqliteLedgerStore store, string tandemHome)
{
    public async ValueTask<RunInspection> InspectAsync(
        Guid runId,
        bool acceptedOnly,
        string? step,
        string? valueType,
        bool includeTools,
        CancellationToken cancellationToken
    )
    {
        var run = await store.GetRunAsync(runId, cancellationToken);
        var entries = await store
            .ForRun(runId)
            .ReadAsync(LedgerPipelineObserver.Journal, cancellationToken);
        var items = entries
            .Select(entry =>
            {
                var accepted = IsAccepted(entry.Value);
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
                    entry.Sequence,
                    0
                );
            })
            .Where(item => !acceptedOnly || item.Category == "accepted")
            .Where(item => string.IsNullOrWhiteSpace(step) || item.StepId == step)
            .Where(item =>
                string.IsNullOrWhiteSpace(valueType)
                || item.ValueType?.Contains(valueType, StringComparison.OrdinalIgnoreCase) is true
            )
            .ToList();

        if (includeTools && !acceptedOnly && string.IsNullOrWhiteSpace(valueType))
        {
            await AddToolEventsAsync(items, runId, step, cancellationToken);
        }
        items.Sort(CompareItems);
        return new RunInspection(run.RunId, run.Composition, run.Status.ToString(), items);
    }

    private async ValueTask AddToolEventsAsync(
        List<RunInspectionItem> items,
        Guid runId,
        string? step,
        CancellationToken cancellationToken
    )
    {
        var eventPath = Path.Combine(tandemHome, "runs", runId.ToString("N"), "events.jsonl");
        if (!File.Exists(eventPath))
        {
            return;
        }
        var telemetryOrder = 0L;
        foreach (var line in await File.ReadAllLinesAsync(eventPath, cancellationToken))
        {
            telemetryOrder++;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var kind = root.TryGetProperty("kind", out var kindValue)
                    ? kindValue.GetString()
                    : null;
                if (
                    kind is not ("tool.started" or "tool.completed")
                    || !root.TryGetProperty("timestamp", out var timestampValue)
                    || !timestampValue.TryGetDateTimeOffset(out var timestamp)
                    || !root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object
                )
                {
                    continue;
                }
                var blockId = root.TryGetProperty("blockId", out var block)
                    ? block.GetString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(step) && blockId != step)
                {
                    continue;
                }
                items.Add(
                    new RunInspectionItem(
                        timestamp,
                        "telemetry",
                        kind,
                        blockId,
                        null,
                        data.TryGetProperty("name", out var name) ? name.GetString() : null,
                        null,
                        null,
                        null,
                        data.Clone(),
                        telemetryOrder,
                        1
                    )
                );
            }
            catch (JsonException)
            {
                // Operational telemetry must not make the durable journal unreadable.
            }
        }
    }

    private static bool IsAccepted(RuntimeJournalRecord record) =>
        record.Kind
            is RuntimeJournalKind.StructuredOutputAccepted
                or RuntimeJournalKind.CapabilityAccepted
                or RuntimeJournalKind.InteractionRequested
                or RuntimeJournalKind.InteractionAnswered
        || record.Kind == RuntimeJournalKind.StepCompleted
            && record.OutcomeKind == StandardOutcomeKinds.Failed
            && record.Payload is not null;

    private static int CompareItems(RunInspectionItem left, RunInspectionItem right)
    {
        var timestamp = left.Timestamp.CompareTo(right.Timestamp);
        if (timestamp != 0)
        {
            return timestamp;
        }
        var source = left.SourceOrder.CompareTo(right.SourceOrder);
        return source != 0 ? source : left.Sequence.CompareTo(right.Sequence);
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
    long Sequence,
    [property: JsonIgnore] int SourceOrder
);
