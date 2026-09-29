using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;
using Tavily;

namespace Tandem.Tests.Infrastructure;

public sealed class TavilyWebToolsTests
{
    [Fact]
    public void Unselected_tools_are_inert()
    {
        var context = Context(search: false, fetch: false);

        TavilyWebTools.Add(context, apiKey: null);

        context.ChatOptions.Tools.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Selected_tool_requires_a_usable_key_before_tool_creation(string? key)
    {
        var context = Context(search: true, fetch: false);

        var create = () => TavilyWebTools.Add(context, key);

        create
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Agent 'selecting-agent'*TAVILY_API_KEY*");
        context.ChatOptions.Tools.Should().BeNull();
    }

    [Theory]
    [InlineData(true, false, "web_search")]
    [InlineData(false, true, "web_fetch")]
    public void Selected_tools_wrap_tavily_under_settled_read_only_names(
        bool search,
        bool fetch,
        string name
    )
    {
        var context = Context(search, fetch);
        var client = new TavilyClient("test-key");
        var tavily = search ? client.AsSearchTool() : client.AsExtractTool();

        TavilyWebTools.Add(context, "test-key");

        var advertised = context
            .ChatOptions.Tools.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<TavilyWebTools.RenamedAIFunction>()
            .Subject;
        advertised.Name.Should().Be(name);
        advertised.Description.Should().Be(tavily.Description);
        advertised.JsonSchema.ToString().Should().Be(tavily.JsonSchema.ToString());
        context.ToolEffects.TryGet(name, out var semantics).Should().BeTrue();
        semantics.Effect.Should().Be(Tandem.Infrastructure.ToolEffect.Read);
        semantics.Evidence.Should().Be(Tandem.Infrastructure.ToolEvidence.None);
    }

    private static AgentImplementationContext Context(bool search, bool fetch) =>
        new(
            "selecting-agent",
            new NoopChatClient(),
            new ChatOptions(),
            new ResolvedAgentWorkspace(
                ".",
                new HashSet<WorkspaceToolKind>(),
                false,
                false,
                search,
                fetch,
                []
            ),
            new ToolEffectRegistry(),
            [],
            null,
            null
        );

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
