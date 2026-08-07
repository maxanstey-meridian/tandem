using System.Collections.Concurrent;

namespace Tandem.Infrastructure;

internal sealed class InMemoryExternalRequestBroker(
    Func<PendingExternalRequest, CancellationToken, ValueTask>? onPending = null
) : IExternalRequestHandler, IAsyncDisposable
{
    private readonly ConcurrentDictionary<RequestKey, PendingWait> _pending = new();
    private readonly object _lifetimeLock = new();
    private bool _disposed;

    public IReadOnlyList<PendingExternalRequest> PendingRequests =>
        _pending.Values.Select(pending => pending.Request).ToArray();

    public int PendingCount => _pending.Count;

    public async ValueTask<ExternalRequestAnswer> WaitAsync(
        PendingExternalRequest request,
        CancellationToken cancellationToken
    )
    {
        var key = new RequestKey(request.RunId, request.RequestId);
        var pending = new PendingWait(request);
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pending.TryAdd(key, pending))
            {
                throw new InvalidOperationException(
                    $"Run/request '{request.RunId:N}/{request.RequestId}' is already pending."
                );
            }
        }

        try
        {
            if (onPending is not null)
            {
                await onPending(request, cancellationToken);
            }
            return await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<RequestKey, PendingWait>(key, pending));
        }
    }

    public void Answer(ExternalRequestAnswer answer)
    {
        var key = new RequestKey(answer.RunId, answer.RequestId);
        PendingWait pending;
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pending.TryRemove(key, out pending!))
            {
                throw new InvalidOperationException(
                    $"Run/request '{answer.RunId:N}/{answer.RequestId}' is not pending."
                );
            }
        }

        if (!pending.Completion.TrySetResult(answer))
        {
            throw new InvalidOperationException(
                $"Run/request '{answer.RunId:N}/{answer.RequestId}' no longer accepts answers."
            );
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

            foreach (var entry in _pending)
            {
                if (_pending.TryRemove(entry))
                {
                    entry.Value.Completion.TrySetCanceled();
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private readonly record struct RequestKey(Guid RunId, string RequestId);

    private sealed class PendingWait(PendingExternalRequest request)
    {
        public PendingExternalRequest Request { get; } = request;

        public TaskCompletionSource<ExternalRequestAnswer> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
