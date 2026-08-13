namespace Tandem.Terminal;

public sealed record TerminalPipelineRunOptions
{
    public IPipelinePersistenceObserver? Persistence { get; init; }

    public IPipelineObserver? Observer { get; init; }

    public PipelineRunOptions Run { get; init; } = new();

    public TerminalDisplayOptions? Display { get; init; }

    public CancellationTokenSource? RunCancellation { get; init; }

    public Func<
        TerminalPipelineCompletion,
        CancellationToken,
        ValueTask
    >? TerminalizingAsync { get; init; }
}

public sealed record TerminalPipelineCompletion(
    TerminalPipelineStatus Status,
    string Summary,
    Exception? Exception = null
);

public static class TerminalPipelineRunner
{
    public static async Task<PipelineRunResult<TState>> RunWithTerminalAsync<TState>(
        this PipelineRunner runner,
        Pipeline<TState> pipeline,
        TState initialState,
        TerminalPipelineRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(initialState);
        options ??= new TerminalPipelineRunOptions();

        var runId = options.Run.RunId ?? Guid.CreateVersion7();
        using var ownedRunCancellation = options.RunCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        using var callerRegistration = options.RunCancellation is null
            ? default
            : cancellationToken.Register(options.RunCancellation.Cancel);
        var runCancellation = options.RunCancellation ?? ownedRunCancellation!;
        var configuredCancel = options.Display?.CancelAsync;
        var displayOptions = (options.Display ?? new TerminalDisplayOptions()) with
        {
            CancelAsync = async token =>
            {
                runCancellation.Cancel();
                if (configuredCancel is not null)
                {
                    await configuredCancel(token);
                }
            },
        };
        await using var display = new TerminalPipelineDisplay(
            pipeline.Inspect(),
            runId,
            displayOptions
        );
        var observer = ComposeObservers(options.Persistence, options.Observer, display.Observer);

        await display.StartAsync(cancellationToken);
        try
        {
            var runOptions = options.Run with { RunId = runId, Observer = observer };
            var result = await runner.RunAsync(
                pipeline,
                initialState,
                runOptions,
                runCancellation.Token
            );
            if (result.Succeeded)
            {
                var summary = result.Outcome?.Summary ?? "Pipeline succeeded";
                await display.SucceededAsync(summary);
                await TerminalizeAsync(TerminalPipelineStatus.Succeeded, summary);
            }
            else
            {
                var summary = result.Outcome?.Summary ?? "Pipeline failed";
                await display.FailedAsync(summary);
                await TerminalizeAsync(TerminalPipelineStatus.Failed, summary);
            }
            await display.WaitForCleanupAsync(CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            await display.CancelledAsync("Run cancelled");
            await TerminalizeAsync(TerminalPipelineStatus.Cancelled, "Run cancelled");
            await display.WaitForCleanupAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await display.FaultedAsync(exception.Message);
            try
            {
                await TerminalizeAsync(
                    TerminalPipelineStatus.Faulted,
                    exception.Message,
                    exception
                );
            }
            catch
            {
                // Preserve the active execution failure when terminalization also fails.
            }
            await display.WaitForCleanupAsync(CancellationToken.None);
            throw;
        }

        ValueTask TerminalizeAsync(
            TerminalPipelineStatus status,
            string summary,
            Exception? exception = null
        ) =>
            options.TerminalizingAsync?.Invoke(
                new TerminalPipelineCompletion(status, summary, exception),
                CancellationToken.None
            ) ?? ValueTask.CompletedTask;
    }

    private static IPipelineObserver ComposeObservers(
        IPipelinePersistenceObserver? persistence,
        IPipelineObserver? observer,
        IPipelineObserver display
    ) =>
        (persistence, observer) switch
        {
            (null, null) => display,
            (null, not null) => new CompositeObserver(observer, display),
            (not null, null) => new CompositePersistenceObserver(persistence, display),
            _ => new CompositePersistenceObserver(
                persistence!,
                new CompositeObserver(observer!, display)
            ),
        };

    private sealed class CompositeObserver(IPipelineObserver first, IPipelineObserver second)
        : IPipelineObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            await first.ObserveAsync(observation, cancellationToken);
            await second.ObserveAsync(observation, cancellationToken);
        }
    }

    private sealed class CompositePersistenceObserver(
        IPipelinePersistenceObserver persistence,
        IPipelineObserver observer
    ) : IPipelinePersistenceObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            await persistence.ObserveAsync(observation, cancellationToken);
            await observer.ObserveAsync(observation, cancellationToken);
        }
    }
}
