using System.Text;
using System.Text.Json;
using FluentAssertions;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class LedgerEntryPageTests
{
    [Fact]
    public async Task Complete_record_is_retrievable_without_knowing_the_missing_diagnostic()
    {
        using var temp = new TempDirectory();
        var store = new SqliteLedgerStore(Path.Combine(temp.Path, "ledger.sqlite3"));
        var run = Guid.NewGuid();
        var observer = await store.CreateObserverAsync(run, "test");
        var original = new string('a', 20000) + "UNKNOWN_FAILURE_😀" + new string('z', 20000);
        await observer.ObserveAsync(
            new PipelineCommandOutput(run, "executor", "task check", original, 1),
            default
        );
        var reader = (IPipelineLedgerReader)store.ForRun(run);
        var listing = await reader.ReadAsync();
        var cursor = listing.Entries.Single().Cursor;
        listing.Entries.Single().Value.Should().NotContain("UNKNOWN_FAILURE");
        var content = new StringBuilder();
        var offset = 0;
        do
        {
            var page = JsonSerializer.SerializeToElement(
                await reader.ReadEntryAsync(cursor, offset, 127)
            );
            content.Append(page.GetProperty("content").GetString());
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }

            var next = page.GetProperty("nextOffset").GetInt32();
            next.Should().BeGreaterThan(offset);
            offset = next;
        } while (true);
        JsonSerializer
            .Deserialize<RuntimeJournalRecord>(
                content.ToString(),
                TandemJson.CreateTypedContract()
            )!
            .Payload!.Value.GetString()
            .Should()
            .Be(original);
        var otherRun = Guid.NewGuid();
        await store.CreateObserverAsync(otherRun, "other");
        await FluentActions
            .Awaiting(async () =>
                await ((IPipelineLedgerReader)store.ForRun(otherRun)).ReadEntryAsync(cursor)
            )
            .Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
    }
}
