using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.OpenAICompatible;

namespace Tandem.Tests.Infrastructure;

public sealed class StreamRetryChatClientTests
{
    [Fact]
    public async Task Retries_when_stream_drops_before_substantive_content()
    {
        var inner = ScriptedStreaming([
            Script.DropAfter([RoleChunk()]),
            Script.Succeed([RoleChunk(), TextChunk("hello"), TextChunk(" world")]),
        ]);
        var client = new StreamRetryChatClient(inner, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var updates = await CollectAsync(
            client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])
        );
        var text = string.Concat(updates);

        text.Should().Be("hello world");
        updates.Should().HaveCount(3);
        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Default_sdk_retry_multiplies_the_attempts_of_this_layer()
    {
        var handler = new CountingHandler(_ => throw new HttpRequestException("reset"));
        var client = new StreamRetryChatClient(
            new SdkStreamingClient(handler, new ImmediateRetryPolicy(maxRetries: 3)),
            maxAttempts: 2,
            retryDelay: TimeSpan.Zero
        );

        var act = () =>
            CollectAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        await act.Should().ThrowAsync<Exception>();
        handler.Requests.Should().Be(8, "each of the 2 attempts here is 4 SDK attempts");
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    [InlineData(HttpStatusCode.TooManyRequests, 3)]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    public async Task With_sdk_retry_disabled_this_client_is_the_only_retry_layer(
        HttpStatusCode? status,
        int expectedRequests
    )
    {
        var handler = new CountingHandler(_ =>
            status is { } code
                ? new HttpResponseMessage(code) { Content = new StringContent("{}") }
                : throw new HttpRequestException("reset")
        );
        var client = new StreamRetryChatClient(
            new SdkStreamingClient(handler, new ClientRetryPolicy(maxRetries: 0)),
            maxAttempts: 3,
            retryDelay: TimeSpan.Zero
        );

        var act = () =>
            CollectAsync(client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        await act.Should().ThrowAsync<Exception>();
        handler.Requests.Should().Be(expectedRequests);
    }

    [Fact]
    public async Task Retries_when_stream_drops_immediately()
    {
        var inner = ScriptedStreaming([
            Script.DropAfter([]),
            Script.DropAfter([]),
            Script.Succeed([TextChunk("recovered")]),
        ]);
        var client = new StreamRetryChatClient(inner, maxAttempts: 4, retryDelay: TimeSpan.Zero);

        var text = string.Concat(
            await CollectAsync(
                client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])
            )
        );

        text.Should().Be("recovered");
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Retries_after_partial_content_without_delivering_the_failed_attempt()
    {
        var inner = ScriptedStreaming([
            Script.DropAfter([RoleChunk(), TextChunk("partial")]),
            Script.Succeed([TextChunk("never")]),
        ]);
        var client = new StreamRetryChatClient(inner, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var updates = await CollectAsync(
            client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])
        );

        string.Concat(updates).Should().Be("never");
        updates.Should().HaveCount(1);
        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Does_not_retry_non_transport_failures()
    {
        var inner = ScriptedStreaming([
            Script.Throw(new InvalidOperationException("bad history")),
            Script.Succeed([TextChunk("never")]),
        ]);
        var client = new StreamRetryChatClient(inner, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var act = async () =>
            await CollectAsync(
                client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Exhausts_attempts_then_throws_original_failure()
    {
        var inner = ScriptedStreaming([
            Script.DropAfter([RoleChunk()]),
            Script.DropAfter([RoleChunk()]),
            Script.DropAfter([RoleChunk()]),
            Script.DropAfter([RoleChunk()]),
        ]);
        var client = new StreamRetryChatClient(inner, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var act = async () =>
            await CollectAsync(
                client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])
            );

        await act.Should().ThrowAsync<IOException>();
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task GetResponse_retries_the_identical_request()
    {
        var inner = FailingThen(
            failTimes: 2,
            response: new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
        );
        var client = new StreamRetryChatClient(inner, maxAttempts: 3, retryDelay: TimeSpan.Zero);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        response.Text.Should().Be("ok");
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Messages_are_snapshotted_before_retry_enumeration()
    {
        var inner = ScriptedStreaming([Script.DropAfter([]), Script.Succeed([TextChunk("ok")])]);
        var client = new StreamRetryChatClient(inner, maxAttempts: 3, retryDelay: TimeSpan.Zero);
        var messages = new CountingMessageList("hi");

        var text = string.Concat(await CollectAsync(client.GetStreamingResponseAsync(messages)));

        text.Should().Be("ok");
        messages.Enumerations.Should().Be(1);
    }

    private static ChatResponseUpdate RoleChunk() =>
        new() { Role = ChatRole.Assistant, Contents = [new TextContent("")] };

    private static ChatResponseUpdate TextChunk(string text) =>
        new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }
        return result;
    }

    private sealed record Script(IReadOnlyList<ChatResponseUpdate>? Updates, Exception? Error)
    {
        public static Script Succeed(params IReadOnlyList<ChatResponseUpdate>[] updates) =>
            new(updates.SelectMany(u => u).ToList(), null);

        public static Script DropAfter(IReadOnlyList<ChatResponseUpdate> updates) =>
            new(updates, new IOException("connection died mid-stream"));

        public static Script Throw(Exception error) => new([], error);
    }

    private sealed class CountingMessageList(string content) : IEnumerable<ChatMessage>
    {
        internal int Enumerations { get; private set; }

        public IEnumerator<ChatMessage> GetEnumerator()
        {
            Enumerations++;
            yield return new ChatMessage(ChatRole.User, content);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private static TestChatClient ScriptedStreaming(params Script[] scripts)
    {
        var call = 0;
        return new TestChatClient
        {
            Stream = (_, _, cancellationToken) =>
                Play(scripts[Math.Min(call++, scripts.Length - 1)], cancellationToken),
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Play(
        Script script,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await Task.Yield();
        foreach (var update in script.Updates ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
        if (script.Error is { } error)
        {
            throw error;
        }
    }

    private static TestChatClient FailingThen(int failTimes, ChatResponse response)
    {
        var call = 0;
        return new TestChatClient
        {
            Respond = (_, _, _) =>
                call++ < failTimes
                    ? throw new IOException("connection died")
                    : Task.FromResult(response),
        };
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ImmediateRetryPolicy(int maxRetries) : ClientRetryPolicy(maxRetries)
    {
        protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) =>
            TimeSpan.Zero;
    }

    // The smallest IChatClient over the real OpenAI SDK pipeline, so the SDK's own retry policy runs.
    private sealed class SdkStreamingClient(
        HttpMessageHandler handler,
        ClientRetryPolicy retryPolicy
    ) : IChatClient
    {
        private readonly OpenAI.Chat.ChatClient _client = new(
            "model",
            new ApiKeyCredential("key"),
            new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri("http://127.0.0.1:1/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = retryPolicy,
            }
        );

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
            await foreach (
                var update in _client.CompleteChatStreamingAsync(
                    [new OpenAI.Chat.UserChatMessage("hi")],
                    cancellationToken: cancellationToken
                )
            )
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    update.ContentUpdate.FirstOrDefault()?.Text
                );
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
