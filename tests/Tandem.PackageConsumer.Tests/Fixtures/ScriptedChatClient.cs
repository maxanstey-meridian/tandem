using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

internal sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new(responses);

    public static ChatResponse Text(string value) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(value)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "package-proof",
        };

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
        foreach (var update in _responses.Dequeue().ToChatResponseUpdates())
        {
            yield return update;
        }
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
