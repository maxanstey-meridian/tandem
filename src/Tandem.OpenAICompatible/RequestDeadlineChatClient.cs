using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Tandem.OpenAICompatible;

/// <summary>Per-attempt deadlines, inside the transport retry boundary.</summary>
public sealed class RequestDeadlineChatClient : DelegatingChatClient
{
    private readonly TimeSpan? _requestTimeout;
    private readonly TimeSpan? _idleTimeout;

    public RequestDeadlineChatClient(
        IChatClient innerClient,
        TimeSpan? requestTimeout = null,
        TimeSpan? idleTimeout = null
    )
        : base(innerClient)
    {
        if (requestTimeout is { } total && total <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
        if (idleTimeout is { } idle && idle <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }
        _requestTimeout = requestTimeout;
        _idleTimeout = idleTimeout;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = _requestTimeout ?? _idleTimeout;
        if (timeout is { } duration)
        {
            deadline.CancelAfter(duration);
        }
        try
        {
            return await InnerClient
                .GetResponseAsync(messages, options, deadline.Token)
                .WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Model response exceeded its per-attempt deadline.");
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_requestTimeout is { } duration)
        {
            total.CancelAfter(duration);
        }
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        var enumerator = InnerClient
            .GetStreamingResponseAsync(messages, options, idle.Token)
            .GetAsyncEnumerator(idle.Token);
        var timedOut = false;
        try
        {
            while (true)
            {
                if (_idleTimeout is { } idleDuration)
                {
                    idle.CancelAfter(idleDuration);
                }
                bool more;
                try
                {
                    more = await enumerator.MoveNextAsync().AsTask().WaitAsync(idle.Token);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested && idle.IsCancellationRequested
                    )
                {
                    timedOut = true;
                    throw new TimeoutException(
                        total.IsCancellationRequested
                            ? "Model stream exceeded its per-attempt deadline."
                            : "Model stream exceeded its inactivity deadline."
                    );
                }
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (!more)
                {
                    yield break;
                }
                yield return enumerator.Current;
            }
        }
        finally
        {
            // Cancel the underlying request before disposal. A misbehaving provider must
            // not turn cleanup into another unbounded wait.
            idle.Cancel();
            try
            {
                await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception) when (timedOut || cancellationToken.IsCancellationRequested) { }
        }
    }
}
