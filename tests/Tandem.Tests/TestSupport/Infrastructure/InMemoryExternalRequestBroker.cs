using System.Collections.Concurrent;
using Tandem.Infrastructure;

namespace Tandem.Tests;

internal sealed class InMemoryExternalRequestBroker(
    Func<PendingExternalRequest, CancellationToken, ValueTask>? onPending = null
) : IExternalRequestHandler, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(Guid RunId, string RequestId), PendingWait> _pending =
        new();
    private bool _disposed;

    public IReadOnlyList<PendingExternalRequest> PendingRequests =>
        _pending.Values.Select(pending => pending.Request).ToArray();

    public int PendingCount => _pending.Count;

    public async ValueTask<ExternalRequestAnswer> WaitAsync(
        PendingExternalRequest request,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = (request.RunId, request.RequestId);
        var pending = new PendingWait(request);
        if (!_pending.TryAdd(key, pending))
        {
            throw new InvalidOperationException(
                $"Run/request '{request.RunId:N}/{request.RequestId}' is already pending."
            );
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
            _pending.TryRemove(new KeyValuePair<(Guid, string), PendingWait>(key, pending));
        }
    }

    public void Answer(ExternalRequestAnswer answer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending.TryRemove((answer.RunId, answer.RequestId), out var pending))
        {
            throw new InvalidOperationException(
                $"Run/request '{answer.RunId:N}/{answer.RequestId}' is not pending."
            );
        }
        pending.Completion.SetResult(answer);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var entry in _pending)
        {
            if (_pending.TryRemove(entry))
            {
                entry.Value.Completion.TrySetCanceled();
            }
        }
        return ValueTask.CompletedTask;
    }

    private sealed class PendingWait(PendingExternalRequest request)
    {
        public PendingExternalRequest Request { get; } = request;
        public TaskCompletionSource<ExternalRequestAnswer> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
