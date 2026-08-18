using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Tandem.OpenAICompatible;

/// <summary>
/// Buffers each streaming response until it completes and retries transport
/// failures. The surrounding agent loop commits an assistant turn only after a
/// complete stream, so withholding updates preserves that atomicity and makes a
/// request safe to re-issue even when the connection drops after partial output.
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
            Exception? failure = null;
            var completed = false;
            var updates = new List<ChatResponseUpdate>();

            var enumerator = InnerClient
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    ChatResponseUpdate? update = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            completed = true;
                            break;
                        }
                        update = enumerator.Current;
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        break;
                    }

                    if (update is null)
                    {
                        continue;
                    }
                    updates.Add(update);
                }
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync();
                }
                catch (Exception error)
                {
                    if (completed)
                    {
                        throw;
                    }
                    failure ??= error;
                }
            }

            if (completed)
            {
                foreach (var update in updates)
                {
                    yield return update;
                }
                yield break;
            }
            if (failure is null)
            {
                yield break;
            }
            if (attempt >= _maxAttempts || !IsRetryable(failure, cancellationToken))
            {
                throw failure;
            }
            await DelayAsync(attempt, cancellationToken);
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

    internal static bool IsRetryable(Exception error, CancellationToken cancellationToken)
    {
        if (error is OperationCanceledException)
        {
            return false;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        return error is IOException or HttpRequestException or TimeoutException
            || error.InnerException is not null
                && IsRetryable(error.InnerException, cancellationToken);
    }
}
