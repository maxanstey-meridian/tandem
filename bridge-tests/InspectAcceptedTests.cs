using System.Text.Json;
using Tandem.Ledger;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class InspectAcceptedTests
{
    [Fact]
    public async Task Inspection_reads_only_accepted_journal_values_in_insert_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-inspect-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var store = new SqliteLedgerStore(path);
            await store.InitializeAsync();
            var runId = Guid.CreateVersion7();
            await store.CreateRunAsync(runId, "inspect");
            var ledger = store.ForRun(runId);
            var other = new LedgerStream<OtherEntry>("application.notes", "application.note");
            await ledger.AppendAsync(other, "note-1", new OtherEntry("not a journal record"));
            await ledger.AppendAsync(
                PipelineJournal.Stream,
                "accepted-1",
                Accepted("first", JsonSerializer.SerializeToElement(new { order = 1 }))
            );
            await ledger.AppendAsync(other, "note-2", new OtherEntry("still not one"));
            await ledger.AppendAsync(
                PipelineJournal.Stream,
                "started",
                new RuntimeJournalRecord(RuntimeJournalKind.StepStarted, "second")
            );
            await ledger.AppendAsync(
                PipelineJournal.Stream,
                "accepted-2",
                Accepted("second", JsonSerializer.SerializeToElement(new { order = 2 }))
            );

            var json = await NodePipelineBridge.InspectAcceptedAsync(path, runId.ToString());

            using var accepted = JsonDocument.Parse(json);
            Assert.Equal(
                ["first", "second"],
                accepted
                    .RootElement.EnumerateArray()
                    .Select(item => item.GetProperty("stepId").GetString())
            );
            Assert.Equal(
                [1, 2],
                accepted
                    .RootElement.EnumerateArray()
                    .Select(item => item.GetProperty("payload").GetProperty("order").GetInt32())
            );
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task Inspection_rejects_an_unknown_run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-inspect-{Guid.NewGuid():N}.sqlite3");
        try
        {
            await new SqliteLedgerStore(path).InitializeAsync();

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                NodePipelineBridge.InspectAcceptedAsync(path, Guid.NewGuid().ToString())
            );
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    private static RuntimeJournalRecord Accepted(string stepId, JsonElement payload) =>
        new(
            RuntimeJournalKind.StructuredOutputAccepted,
            stepId,
            ValueType: "order",
            Payload: payload
        );

    private sealed record OtherEntry(string Text);
}
