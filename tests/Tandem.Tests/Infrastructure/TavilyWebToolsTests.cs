using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class TavilyWebToolsTests
{
    [Fact]
    public void Unselected_tools_are_inert()
    {
        var environmentRead = false;
        var context = Context(search: false, fetch: false);

        TavilyWebTools.Add(
            context,
            (_, _, _) => throw new InvalidOperationException("Tools must not be created."),
            _ =>
            {
                environmentRead = true;
                return null;
            }
        );

        environmentRead.Should().BeFalse();
        context.ChatOptions.Tools.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Selected_tool_requires_a_usable_key_before_tool_creation(string? key)
    {
        var context = Context(search: true, fetch: false);
        var create = () =>
            TavilyWebTools.Add(
                context,
                (_, _, _) => throw new InvalidOperationException("Tools must not be created."),
                _ => key
            );

        create
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Agent 'selecting-agent'*TAVILY_API_KEY*");
    }

    [Fact]
    public async Task Selected_tools_preserve_wrappers_and_are_read_only_without_evidence()
    {
        var searchCalls = 0;
        var fetchCalls = 0;
        var search = AIFunctionFactory.Create(
            (string query) =>
            {
                searchCalls++;
                return query;
            },
            "tavily_search",
            "Search via Tavily."
        );
        var fetch = AIFunctionFactory.Create(
            (string urls) =>
            {
                fetchCalls++;
                return urls;
            },
            "tavily_extract",
            "Extract supplied URLs via Tavily."
        );
        var context = Context(search: true, fetch: false);

        TavilyWebTools.Add(context, (key, _, _) => (search, fetch), _ => "test-key");

        var advertised = context.ChatOptions.Tools.Should().ContainSingle().Subject;
        advertised.Name.Should().Be("web_search");
        advertised.Description.Should().Be(search.Description);
        var function = advertised.Should().BeAssignableTo<AIFunction>().Subject;
        function.JsonSchema.ToString().Should().Be(search.JsonSchema.ToString());
        await function.InvokeAsync(new AIFunctionArguments { ["query"] = "Tandem" });
        searchCalls.Should().Be(1);
        fetchCalls.Should().Be(0);
        context.ToolEffects.TryGet("web_search", out var semantics).Should().BeTrue();
        semantics.Effect.Should().Be(Tandem.Infrastructure.ToolEffect.Read);
        semantics.Evidence.Should().Be(Tandem.Infrastructure.ToolEvidence.None);
    }

    [Fact]
    public async Task Fetch_delegates_under_the_settled_name_and_is_read_only_without_evidence()
    {
        var fetchCalls = 0;
        var context = Context(search: false, fetch: true);
        TavilyWebTools.Add(
            context,
            (_, _, _) =>
                (
                    AIFunctionFactory.Create(() => "search", "search"),
                    AIFunctionFactory.Create(
                        (string urls) =>
                        {
                            fetchCalls++;
                            return urls;
                        },
                        "extract"
                    )
                ),
            _ => "test-key"
        );

        var advertised = context.ChatOptions.Tools.Should().ContainSingle().Subject;
        advertised.Name.Should().Be("web_fetch");
        var function = advertised.Should().BeAssignableTo<AIFunction>().Subject;
        await function.InvokeAsync(new AIFunctionArguments { ["urls"] = "https://example.com" });
        fetchCalls.Should().Be(1);
        context.ToolEffects.TryGet("web_fetch", out var semantics).Should().BeTrue();
        semantics.Effect.Should().Be(Tandem.Infrastructure.ToolEffect.Read);
        semantics.Evidence.Should().Be(Tandem.Infrastructure.ToolEvidence.None);
    }

    [Fact]
    public void Harness_fails_selected_web_tools_before_model_execution_when_key_is_missing()
    {
        var context = Context(search: true, fetch: false);

        var create = () =>
            HarnessAgentImplementation.Create(
                context,
                "Harness.",
                (_, _, _) => throw new InvalidOperationException("Tools must not be created."),
                _ => "   "
            );

        create
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Agent 'selecting-agent'*TAVILY_API_KEY*");
    }

    [Fact]
    public void Harness_advertises_only_selected_web_tools_under_settled_names()
    {
        var context = Context(search: false, fetch: true);

        _ = HarnessAgentImplementation.Create(
            context,
            "Harness.",
            (_, _, _) =>
                (
                    AIFunctionFactory.Create(() => "search", "tavily_search"),
                    AIFunctionFactory.Create(() => "fetch", "tavily_extract")
                ),
            _ => "test-key"
        );

        context.ChatOptions.Tools.Should().ContainSingle(tool => tool.Name == "web_fetch");
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
