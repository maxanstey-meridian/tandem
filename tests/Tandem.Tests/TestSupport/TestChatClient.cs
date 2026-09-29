using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Tandem.Tests;

/// <summary>
/// The one scripted <see cref="IChatClient"/> for tests: replies from a queue unless a
/// <see cref="Respond"/> or <see cref="Stream"/> delegate is supplied, and records every request.
/// </summary>
internal sealed class TestChatClient(params ChatResponse[] responses) : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new(responses);
    private readonly Lock _gate = new();

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
    public List<ChatOptions?> Options { get; } = [];
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return Requests.Count;
            }
        }
    }

    /// <summary>The tool names offered on each request.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AdvertisedTools
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. Options.Select(options =>
                        (IReadOnlyList<string>)(
                            options?.Tools?.Select(tool => tool.Name).ToArray() ?? []
                        )
                    ),
                ];
            }
        }
    }

    public Func<
        IReadOnlyList<ChatMessage>,
        ChatOptions?,
        CancellationToken,
        Task<ChatResponse>
    >? Respond { get; init; }

    public Func<
        IReadOnlyList<ChatMessage>,
        ChatOptions?,
        CancellationToken,
        IAsyncEnumerable<ChatResponseUpdate>
    >? Stream { get; init; }

    public Action? OnRequest { get; init; }

    public ChatClientMetadata? Metadata { get; init; }

    public static TestChatClient Replying(params string[] texts) => new([.. texts.Select(Text)]);

    public static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    public static ChatResponse ToolCall(
        string name,
        IDictionary<string, object?>? arguments = null,
        string? callId = null
    ) =>
        new(
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        callId ?? Guid.CreateVersion7().ToString("N"),
                        name,
                        arguments ?? new Dictionary<string, object?>()
                    ),
                ]
            )
        )
        {
            FinishReason = ChatFinishReason.ToolCalls,
            ModelId = "test-model",
        };

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var request = Record(messages, options);
        return Respond is null ? Dequeue() : await Respond(request, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (Stream is null)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
            yield break;
        }
        var request = Record(messages, options);
        await foreach (
            var update in Stream(request, options, cancellationToken)
                .WithCancellation(cancellationToken)
        )
        {
            yield return update;
        }
    }

    /// <summary>Tool results the client was sent, by call id (the latest wins).</summary>
    public IReadOnlyDictionary<string, JsonElement> ToolResults()
    {
        lock (_gate)
        {
            return Requests
                .SelectMany(request => request)
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .GroupBy(result => result.CallId)
                .ToDictionary(
                    group => group.Key,
                    group =>
                        group.Last().Result is JsonElement json
                            ? json
                            : JsonSerializer.SerializeToElement(group.Last().Result)
                );
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? Metadata : null;

    public void Dispose() { }

    private ChatMessage[] Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var request = messages.ToArray();
        lock (_gate)
        {
            Requests.Add(request);
            Options.Add(options?.Clone());
        }
        OnRequest?.Invoke();
        return request;
    }

    private ChatResponse Dequeue()
    {
        lock (_gate)
        {
            return _responses.TryDequeue(out var response)
                ? response
                : throw new InvalidOperationException(
                    "TestChatClient has no scripted response left."
                );
        }
    }
}
