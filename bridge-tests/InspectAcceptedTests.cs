using System.Text.Json;
using Tandem.Ledger;
using Xunit;

namespace Tandem.Bridge;

public sealed class InspectAcceptedTests
{
    [Fact]
    public async Task Inspection_reads_only_accepted_journal_values_in_insert_order()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-inspect-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var store = new SqliteLedgerStore(path);
            var runId = Guid.CreateVersion7();
            var observer = await store.CreateObserverAsync(runId, "inspect");
            await observer.ObserveAsync(Accepted(runId, "first", 1), default);
            await observer.ObserveAsync(new PipelineStepStarted(runId, "second"), default);
            await observer.ObserveAsync(Accepted(runId, "second", 2), default);

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

    private static PipelineStructuredOutputAccepted Accepted(
        Guid runId,
        string stepId,
        int order
    ) =>
        new(
            runId,
            stepId,
            $"{stepId}-output",
            StandardOutcomeKinds.Success,
            "order",
            JsonSerializer.SerializeToElement(new { order })
        );
}
