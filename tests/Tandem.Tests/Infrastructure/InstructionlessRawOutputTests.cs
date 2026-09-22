using System.Runtime.CompilerServices;
using FluentValidation;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class InstructionlessRawOutputTests
{
    [Fact]
    public async Task Raw_agent_can_send_only_the_authored_user_message()
    {
        using var client = new UserOnlyClient();
        var agent = Agent
            .Create<string>("raw", "", client)
            .WithMessage(state => state)
            .WithRawOutput(new RawText(), (_, value) => value)
            .Build();
        var result = await new PipelineRunner().RunAsync(
            Pipeline.Start(agent, "user-only").Build(agent),
            "Mr. Burns won by 0.2 seconds."
        );
        Assert.Equal("accepted", result.State);
        Assert.True(client.Called);
    }

    [Fact]
    public void Normal_agent_still_requires_instructions_when_built()
    {
        using var client = new UserOnlyClient();
        Assert.Throws<ArgumentException>(() =>
            Agent.Create<string>("normal", "", client).WithMessage(state => state).Build()
        );
        Assert.Throws<ArgumentNullException>(() => Agent.Create<string>("normal", null!, client));
    }

    private sealed class RawText : IAgentRawOutputDefinition<string, string>
    {
        public string Instructions => "";
        public IValidator<string> Validator { get; } = new InlineValidator<string>();

        public string Parse(string response) => response;
    }

    private sealed class UserOnlyClient : IChatClient
    {
        public bool Called { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Called = true;
            Assert.True(string.IsNullOrWhiteSpace(options?.Instructions));
            Assert.Null(options?.ResponseFormat);
            var message = Assert.Single(messages);
            Assert.Equal(ChatRole.User, message.Role);
            Assert.Equal("Mr. Burns won by 0.2 seconds.", message.Text);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "accepted");
            await Task.CompletedTask;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
