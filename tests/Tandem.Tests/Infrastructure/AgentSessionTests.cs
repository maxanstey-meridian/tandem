using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Infrastructure;

public sealed class AgentSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionHistory_IsFreshByDefault_AndRetainedOnlyWhenExplicit(
        bool continueSession
    )
    {
        var client = new RecordingChatClient("first response", "second response");
        var block = new AgentBlock<TestState>(
            new AgentBlockConfig<TestState>(
                "agent",
                "agent",
                "Respond.",
                [],
                state => $"request {state.Count}",
                null,
                null,
                ContinueSession: continueSession
            ),
            client
        );
        var first = await block.ExecuteAsync(
            new PipelineMessage<TestState>(
                PipelineRuntime.Create(Guid.CreateVersion7()),
                new TestState(1)
            ),
            CancellationToken.None
        );

        await block.ExecuteAsync(
            first with
            {
                State = new TestState(2),
                LatestOutcome = null,
            },
            CancellationToken.None
        );

        client.Requests.Should().HaveCount(2);
        var retained = client
            .Requests[1]
            .Any(message =>
                message.Role == ChatRole.Assistant
                && message.Text.Contains("first response", StringComparison.Ordinal)
            );
        retained.Should().Be(continueSession);
    }

    private sealed record TestState(int Count);

    private sealed class RecordingChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Requests.Add(messages.ToArray());
            var response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [new TextContent(_responses.Dequeue())])
            )
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "test-model",
            };
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
