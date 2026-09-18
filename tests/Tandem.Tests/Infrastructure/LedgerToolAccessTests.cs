using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class LedgerToolAccessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistence_DoesNotGrantLedgerToolsUnlessEnabled(bool enabled)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"tandem-ledger-access-{Guid.NewGuid():N}"
        );
        try
        {
            var path = Path.Combine(directory, "ledger.sqlite3");
            using var client = new RecordingClient();
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
                .Tools.Should()
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
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public sealed record ProbeState(string Message);

    private sealed class RecordingClient : IChatClient
    {
        public string[] Tools { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Tools = options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (
                var update in (
                    await GetResponseAsync(messages, options, cancellationToken)
                ).ToChatResponseUpdates()
            )
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
