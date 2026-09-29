using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Tandem.OpenAICompatible;

/// <summary>
/// Buffers each streaming response until it completes and retries transport failures and
/// retryable HTTP statuses. The surrounding agent loop commits an assistant turn only after a
/// complete stream, so withholding updates preserves that atomicity and makes a request safe to
/// re-issue even when the connection drops after partial output. This is the only retry layer:
/// build the inner OpenAI client with <c>RetryPolicy = new ClientRetryPolicy(0)</c>, otherwise
/// every attempt here is multiplied by the SDK's own retries.
/// </summary>
public sealed class StreamRetryChatClient(
    IChatClient innerClient,
    int maxAttempts = 4,
    TimeSpan? retryDelay = null
) : DelegatingChatClient(innerClient)
{
    private readonly int _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var snapshot = messages.ToList();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await InnerClient.GetResponseAsync(snapshot, options, cancellationToken);
            }
            catch (Exception error)
                when (attempt < _maxAttempts && IsRetryable(error, cancellationToken))
            {
                await DelayAsync(attempt, cancellationToken);
            }
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => StreamWithRetryAsync(messages.ToList(), options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamWithRetryAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            var updates = new List<ChatResponseUpdate>();
            try
            {
                await foreach (
                    var update in InnerClient
                        .GetStreamingResponseAsync(messages, options, cancellationToken)
                        .WithCancellation(cancellationToken)
                )
                {
                    if (update is not null)
                    {
                        updates.Add(update);
                    }
                }
            }
            catch (Exception error)
                when (attempt < _maxAttempts && IsRetryable(error, cancellationToken))
            {
                await DelayAsync(attempt, cancellationToken);
                continue;
            }
            foreach (var update in updates)
            {
                yield return update;
            }
            yield break;
        }
    }

    private async Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromTicks(_retryDelay.Ticks * (1L << Math.Min(attempt - 1, 5)));
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    internal static bool IsRetryable(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && error is not OperationCanceledException
        && (
            error
                is IOException
                    or HttpRequestException
                    or TimeoutException
                    or ClientResultException { Status: 408 or 429 or >= 500 }
            || error.InnerException is not null
                && IsRetryable(error.InnerException, cancellationToken)
        );
}
