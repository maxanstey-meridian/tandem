using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Infrastructure;

public sealed class ToolResultAdjacencyChatClientTests
{
    [Fact]
    public void Normalize_moves_deferred_tool_result_next_to_its_call()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "instructions"),
            new(ChatRole.User, "packet"),
            AssistantCall("call_1", "read_ledger"),
            ToolResult("call_1", "ledger contents"),
            AssistantCall("call_2", "write_checkpoint"),
            new(ChatRole.User, "next invocation message"),
            ToolResult("call_2", """{"accepted":true}"""),
        };

        var normalized = ToolResultAdjacencyChatClient.Normalize(history);

        normalized
            .Select(message => message.Role)
            .Should()
            .Equal(
                ChatRole.System,
                ChatRole.User,
                ChatRole.Assistant,
                ChatRole.Tool,
                ChatRole.Assistant,
                ChatRole.Tool,
                ChatRole.User
            );
        normalized[5]
            .Contents.OfType<FunctionResultContent>()
            .Single()
            .CallId.Should()
            .Be("call_2");
        normalized[6].Text.Should().Be("next invocation message");
    }

    [Fact]
    public void Normalize_keeps_parallel_results_contiguous_after_their_call()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "packet"),
            new(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call_a",
                        "read_ledger",
                        new Dictionary<string, object?> { ["q"] = "x" }
                    ),
                    new FunctionCallContent(
                        "call_b",
                        "file_access_grep",
                        new Dictionary<string, object?> { ["pattern"] = "y" }
                    ),
                ]
            ),
            ToolResult("call_a", "a result"),
            ToolResult("call_b", "b result"),
        };

        var normalized = ToolResultAdjacencyChatClient.Normalize(history);

        normalized.Should().HaveCount(4);
        normalized[2]
            .Contents.OfType<FunctionResultContent>()
            .Single()
            .CallId.Should()
            .Be("call_a");
        normalized[3]
            .Contents.OfType<FunctionResultContent>()
            .Single()
            .CallId.Should()
            .Be("call_b");
    }

    [Fact]
    public void Normalize_moves_parallel_results_displaced_by_a_user_message()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "packet"),
            new(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call_a",
                        "read_ledger",
                        new Dictionary<string, object?> { ["q"] = "x" }
                    ),
                    new FunctionCallContent(
                        "call_b",
                        "file_access_grep",
                        new Dictionary<string, object?> { ["pattern"] = "y" }
                    ),
                ]
            ),
            new(ChatRole.User, "interposed"),
            ToolResult("call_a", "a result"),
            ToolResult("call_b", "b result"),
        };

        var normalized = ToolResultAdjacencyChatClient.Normalize(history);

        normalized
            .Select(message => message.Role)
            .Should()
            .Equal(ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Tool, ChatRole.User);
        normalized[2]
            .Contents.OfType<FunctionResultContent>()
            .Single()
            .CallId.Should()
            .Be("call_a");
        normalized[3]
            .Contents.OfType<FunctionResultContent>()
            .Single()
            .CallId.Should()
            .Be("call_b");
        normalized[4].Text.Should().Be("interposed");
    }

    [Fact]
    public void Normalize_drops_orphaned_results_from_full_history()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "[Tool Calls] write_checkpoint:"),
            new(ChatRole.User, "next invocation message"),
            ToolResult("call_compacted", "accepted"),
        };

        var normalized = ToolResultAdjacencyChatClient.Normalize(history);

        normalized
            .Select(message => message.Role)
            .Should()
            .Equal(ChatRole.Assistant, ChatRole.User);
    }

    [Fact]
    public void Normalize_keeps_output_only_history_for_server_managed_conversations()
    {
        var history = new List<ChatMessage>
        {
            ToolResult("call_remote", "result held server-side"),
        };

        var normalized = ToolResultAdjacencyChatClient.Normalize(history);

        normalized.Should().BeSameAs(history);
    }

    [Fact]
    public void Normalize_leaves_plain_histories_untouched()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "instructions"),
            new(ChatRole.User, "packet"),
            new(ChatRole.Assistant, "prose"),
            new(ChatRole.User, "reply"),
        };

        var normalized = ToolResultAdjacencyChatClient.Normalize(history);

        normalized.Should().BeSameAs(history);
    }

    [Fact]
    public async Task GetResponse_passes_normalized_history_to_the_inner_client()
    {
        var inner = new CapturingChatClient();
        var client = new ToolResultAdjacencyChatClient(inner);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "packet"),
            AssistantCall("call_1", "write_checkpoint"),
            new(ChatRole.User, "next invocation message"),
            ToolResult("call_1", """{"accepted":true}"""),
        };

        await client.GetResponseAsync(history);

        inner
            .SeenMessages!.Select(message => message.Role)
            .Should()
            .Equal(ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User);
    }

    [Fact]
    public async Task GetService_resolves_metadata_from_the_inner_client()
    {
        var inner = new MetadataChatClient();
        var client = new ToolResultAdjacencyChatClient(inner);

        client.GetService<ChatClientMetadata>().Should().BeSameAs(inner.Metadata);
    }

    private static ChatMessage AssistantCall(string callId, string name) =>
        new(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    callId,
                    name,
                    new Dictionary<string, object?> { ["input"] = "x" }
                ),
            ]
        );

    private static ChatMessage ToolResult(string callId, string result) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    private sealed class CapturingChatClient : IChatClient
    {
        internal IReadOnlyList<ChatMessage>? SeenMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            SeenMessages = messages.ToList();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) =>
            GetResponseAsync(messages, options, cancellationToken)
                .Result.ToChatResponseUpdates()
                .ToAsyncEnumerable();

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class MetadataChatClient : IChatClient
    {
        internal ChatClientMetadata Metadata { get; } = new("gpt-test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) =>
            GetResponseAsync(messages, options, cancellationToken)
                .Result.ToChatResponseUpdates()
                .ToAsyncEnumerable();

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;
    }
}
