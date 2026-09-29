using FluentAssertions;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class LedgerToolAccessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistence_DoesNotGrantLedgerToolsUnlessEnabled(bool enabled)
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var path = Path.Combine(directory, "ledger.sqlite3");
        using var client = TestChatClient.Replying("Done.");
        var agent = Agent
            .Create<ProbeState>("probe", "Respond briefly.", client)
            .WithMessage(state => state.Message)
            .Build();
        var pipeline = Pipeline.Start(agent, "ledger-access").Persist().Build(agent);
        var options = new SqlitePipelineRunOptions(path);
        if (enabled)
        {
            options = options with { EnableLedgerTools = true };
        }
        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new ProbeState("Hello"),
            options
        );

        result.Succeeded.Should().BeTrue();
        client
            .AdvertisedTools.Single()
            .Should()
            .BeEquivalentTo(
                enabled
                    ? ["read_ledger", "read_ledger_entry", "search_ledger"]
                    : Array.Empty<string>()
            );
        var store = new SqliteLedgerStore(path);
        (await store.GetRunAsync(result.RunId)).Status.Should().Be(LedgerRunStatus.Ready);
        (await store.ReadLatestAcceptedAsync<ProbeState>(result.RunId, agent.Id))
            .Should()
            .NotBeNull();
    }

    public sealed record ProbeState(string Message);
}
