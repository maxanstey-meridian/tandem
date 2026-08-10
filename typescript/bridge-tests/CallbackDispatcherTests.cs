using System.Collections.Concurrent;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class CallbackDispatcherTests
{
    [Fact]
    public void Invoke_OnJavaScriptContext_UsesSynchronousCallbackDirectly()
    {
        var context = new SynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var asyncCalled = false;
            var dispatcher = new CallbackDispatcher(
                context,
                (id, state, input) => Success($"{id}:{state}:{input}"),
                (_, _, _, _) =>
                {
                    asyncCalled = true;
                    return Task.FromResult(Success("unexpected"));
                },
                CancellationToken.None
            );

            var result = dispatcher.Invoke("callback", "state", "input");

            Assert.Equal("callback:state:input", result);
            Assert.False(asyncCalled);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void Invoke_OffJavaScriptContext_MarshalsAndReturnsResult()
    {
        using var context = new WorkerSynchronizationContext();
        var callbackThreadId = 0;
        SynchronizationContext? callbackContext = null;
        var dispatcher = new CallbackDispatcher(
            context,
            (_, _, _) =>
            {
                callbackThreadId = Environment.CurrentManagedThreadId;
                callbackContext = SynchronizationContext.Current;
                return Success("result");
            },
            (_, _, _, _) => Task.FromResult(Success("unexpected")),
            CancellationToken.None
        );

        var result = dispatcher.Invoke("callback", "state", "input");

        Assert.Equal("result", result);
        Assert.Equal(context.ThreadId, callbackThreadId);
        Assert.Same(context, callbackContext);
        Assert.NotEqual(Environment.CurrentManagedThreadId, callbackThreadId);
    }

    [Fact]
    public void Invoke_OffJavaScriptContext_RethrowsOriginalException()
    {
        using var context = new WorkerSynchronizationContext();
        var expected = new InvalidOperationException("callback failed");
        var dispatcher = new CallbackDispatcher(
            context,
            (_, _, _) => throw expected,
            (_, _, _, _) => Task.FromResult(Success("unexpected")),
            CancellationToken.None
        );

        var actual = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Invoke("callback", "state", "input")
        );

        Assert.Same(expected, actual);
    }

    [Fact]
    public void Invoke_AbandonedJavaScriptContext_StopsWhenRunIsCancelled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var dispatcher = new CallbackDispatcher(
            new AbandonedSynchronizationContext(),
            (_, _, _) => Success("unexpected"),
            (_, _, _, _) => Task.FromResult(Success("unexpected")),
            cancellation.Token
        );

        Assert.Throws<OperationCanceledException>(() =>
            dispatcher.Invoke("callback", "state", "input")
        );
    }

    [Fact]
    public async Task Invoke_CancelledBeforeDelayedDispatch_DoesNotFaultJavaScriptContext()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new DelayedSynchronizationContext();
        var callbackCalls = 0;
        var dispatcher = new CallbackDispatcher(
            context,
            (_, _, _) =>
            {
                callbackCalls++;
                return Success("late");
            },
            (_, _, _, _) => Task.FromResult(Success("unexpected")),
            cancellation.Token
        );
        var invocation = Task.Run(() => dispatcher.Invoke("callback", "state", "input"));
        await context.Posted.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => invocation);

        var dispatchFailure = Record.Exception(context.Dispatch);
        Assert.Null(dispatchFailure);
        Assert.Equal(0, callbackCalls);
    }

    private static string Success(string value) =>
        System.Text.Json.JsonSerializer.Serialize(new { succeeded = true, value });

    private sealed class AbandonedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    private sealed class DelayedSynchronizationContext : SynchronizationContext
    {
        private (SendOrPostCallback Callback, object? State)? _pending;
        private readonly TaskCompletionSource _posted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _pending = (d, state);
            _posted.TrySetResult();
        }

        public void Dispatch()
        {
            var pending =
                _pending ?? throw new InvalidOperationException("No callback was posted.");
            pending.Callback(pending.State);
        }
    }

    private sealed class WorkerSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work =
            new();
        private readonly Thread _thread;

        public WorkerSynchronizationContext()
        {
            using var started = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                ThreadId = Environment.CurrentManagedThreadId;
                started.Set();
                foreach (var (callback, state) in _work.GetConsumingEnumerable())
                {
                    callback(state);
                }
            })
            {
                IsBackground = true,
            };
            _thread.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "Worker context did not start.");
        }

        public int ThreadId { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => _work.Add((d, state));

        public void Dispose()
        {
            _work.CompleteAdding();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "Worker context did not stop.");
            _work.Dispose();
        }
    }
}
