using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Tandem.Tests.Infrastructure;

public sealed class FilteringAIContextProviderTests
{
    [Fact]
    public async Task Filtering_PreservesContextSessionAndInnerLifecycle()
    {
        var existing = Tool("existing");
        var selected = Tool("read_file");
        var hidden = Tool("write_file");
        var inner = new RecordingProvider(selected, hidden);
        var provider = new FilteringAIContextProvider(inner, new HashSet<string> { "read_file" });
        var agent = new ChatClientAgent(
            new NoopChatClient(),
            new ChatClientAgentOptions { Id = "agent", Name = "agent" }
        );
        var session = await agent.CreateSessionAsync();
        var context = new AIContext { Instructions = "original", Tools = [existing] };

        var result = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, session, context),
            CancellationToken.None
        );
        await provider.InvokedAsync(
            new AIContextProvider.InvokedContext(agent, session, [], []),
            CancellationToken.None
        );

        result.Instructions.Should().Be("from inner");
        result.Tools!.Select(tool => tool.Name).Should().Equal("existing", "read_file");
        inner.Session.Should().BeSameAs(session);
        inner.Invoked.Should().BeTrue();
    }

    private static AIFunction Tool(string name) =>
        AIFunctionFactory.Create(() => name, name, $"Run {name}.");

    private sealed class RecordingProvider(params AITool[] tools) : AIContextProvider
    {
        internal AgentSession? Session { get; private set; }
        internal bool Invoked { get; private set; }

        protected override ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken
        )
        {
            Session = context.Session;
            context.AIContext.Instructions = "from inner";
            context.AIContext.Tools = tools;
            return ValueTask.FromResult(context.AIContext);
        }

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken
        )
        {
            Invoked = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopChatClient : IChatClient
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
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
