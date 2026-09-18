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
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteLedgerStore(Path.Combine(directory, "ledger.sqlite3"));
            await store.InitializeAsync();
            var run = Guid.NewGuid();
            await store.CreateRunAsync(run, "test");
            var ledger = store.ForRun(run);
            var original = new string('a', 20000) + "UNKNOWN_FAILURE_😀" + new string('z', 20000);
            await ledger.AppendAsync(
                new LedgerStream<string>("diagnostics", "test.output"),
                "output",
                original
            );
            var reader = (IPipelineLedgerReader)ledger;
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
            JsonSerializer.Deserialize<string>(content.ToString()).Should().Be(original);
            var otherRun = Guid.NewGuid();
            await store.CreateRunAsync(otherRun, "other");
            await FluentActions
                .Awaiting(async () =>
                    await ((IPipelineLedgerReader)store.ForRun(otherRun)).ReadEntryAsync(cursor)
                )
                .Should()
                .ThrowAsync<ArgumentOutOfRangeException>();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
