using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Infrastructure;

public sealed class AgentSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_ComesFromExplicitTimeoutOrHostLifetime(bool explicitTimeout)
    {
        var block = new AgentBlock<TestState>(
            new AgentBlockConfig<TestState>(
                "agent",
                "agent",
                "Respond.",
                [],
                _ => "request",
                null,
                null,
                Timeout: explicitTimeout ? TimeSpan.FromMilliseconds(20) : null
            ),
            new BlockingChatClient()
        );
        using var hostCancellation = new CancellationTokenSource();
        if (!explicitTimeout)
        {
            hostCancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
        }

        var execute = async () =>
            await block.ExecuteAsync(
                new PipelineMessage<TestState>(
                    PipelineRuntime.Create(Guid.CreateVersion7()),
                    new TestState(0)
                ),
                hostCancellation.Token
            );

        await execute.Should().ThrowAsync<OperationCanceledException>();
    }

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
        client
            .Instructions.Should()
            .OnlyContain(instructions =>
                instructions.Contains(
                    "one bounded node in a Tandem pipeline",
                    StringComparison.Ordinal
                )
                && !instructions.Contains("repository", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("workspace", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("packet", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("mutation", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("planner", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("reviewer", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("executor", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("verification", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("coding evidence", StringComparison.OrdinalIgnoreCase)
                && !instructions.Contains("publication", StringComparison.OrdinalIgnoreCase)
            );
        var retained = client
            .Requests[1]
            .Any(message =>
                message.Role == ChatRole.Assistant
                && message.Text.Contains("first response", StringComparison.Ordinal)
            );
        retained.Should().Be(continueSession);
    }

    [Fact]
    public async Task TypedExamples_AreFreshSessionTurns_AndAreNotResentForCorrectionOrRetention()
    {
        var client = new RecordingChatClient("{\"value\":0}", "{\"value\":1}", "{\"value\":2}");
        var agent = Agent
            .Create<ExampleState>("agent", "Respond.", client)
            .WithMessage(state => $"live request {state.Value}")
            .WithOutput(
                new ExampleOutputDefinition(),
                (state, output) => state with { Value = output.Value }
            )
            .ContinueSession()
            .WithMessageAugmentation((_, _) => ValueTask.FromResult<string?>("first augmentation"))
            .WithMessageAugmentation((_, _) => ValueTask.FromResult<string?>("second augmentation"))
            .Build();
        var complete = PipelineNodes.Complete<ExampleState>("complete");
        var pipeline = Pipeline
            .Start(agent, "typed-example-session")
            .Route(agent.Success, state => state.Value < 2, agent, "continue")
            .Route(agent.Success, state => state.Value == 2, complete, "complete")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(pipeline, new ExampleState(0));

        result.State.Value.Should().Be(2);
        client.Requests.Should().HaveCount(3);
        client
            .Requests[0]
            .Select(message => (message.Role, message.Text))
            .Should()
            .ContainInOrder(
                (ChatRole.User, "example input"),
                (ChatRole.Assistant, "{\"value\":7}"),
                (ChatRole.User, "live request 0\n\nfirst augmentation\n\nsecond augmentation")
            );
        client
            .Requests[1]
            .Last(message => message.Role == ChatRole.User)
            .Text.Should()
            .Contain("greater than '0'");
        client
            .Requests[2]
            .Last(message => message.Role == ChatRole.User)
            .Text.Should()
            .Be("live request 1\n\nfirst augmentation\n\nsecond augmentation");
        client
            .Requests.Should()
            .OnlyContain(request =>
                request.Count(message =>
                    message.Role == ChatRole.User && message.Text == "example input"
                ) == 1
                && request.Count(message =>
                    message.Role == ChatRole.Assistant && message.Text == "{\"value\":7}"
                ) == 1
            );
    }

    private sealed record TestState(int Count);

    private sealed record ExampleState(int Value);

    private sealed record ExampleOutput(int Value);

    private sealed class ExampleOutputDefinition
        : IAgentOutputDefinition<ExampleState, ExampleOutput>
    {
        public string Instructions => "Return a positive value.";
        public IValidator<ExampleOutput> Validator { get; } = CreateValidator();

        public IReadOnlyList<AgentOutputExample<ExampleOutput>> Examples(ExampleState state) =>
            [new("example input", new ExampleOutput(7))];

        private static IValidator<ExampleOutput> CreateValidator()
        {
            var validator = new InlineValidator<ExampleOutput>();
            validator.RuleFor(output => output.Value).GreaterThan(0);
            return validator;
        }
    }

    private sealed class RecordingChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<string> Instructions { get; } = [];

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
            Instructions.Add(options?.Instructions ?? "");
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

    private sealed class BlockingChatClient : IChatClient
    {
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
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
