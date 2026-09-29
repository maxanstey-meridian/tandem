namespace Tandem.Terminal;

public sealed record TerminalPipelineRunOptions
{
    /// <summary>The run's options; the display observes after <see cref="PipelineRunOptions.Observer"/>.</summary>
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

        await display.StartAsync(cancellationToken);
        try
        {
            var runOptions = options.Run with
            {
                RunId = runId,
                Observer = PipelineObservers.Compose(options.Run.Observer, display.Observer),
            };
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
        catch (OperationCanceledException exception) when (runCancellation.IsCancellationRequested)
        {
            await display.CancelledAsync("Run cancelled");
            await TerminalizeFailureAsync(
                TerminalPipelineStatus.Cancelled,
                "Run cancelled",
                exception
            );
            await display.WaitForCleanupAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await display.FaultedAsync(exception.Message);
            await TerminalizeFailureAsync(
                TerminalPipelineStatus.Faulted,
                exception.Message,
                exception
            );
            await display.WaitForCleanupAsync(CancellationToken.None);
            throw;
        }

        async ValueTask TerminalizeFailureAsync(
            TerminalPipelineStatus status,
            string summary,
            Exception activeFailure
        )
        {
            try
            {
                await TerminalizeAsync(
                    status,
                    summary,
                    status == TerminalPipelineStatus.Faulted ? activeFailure : null
                );
            }
            catch (Exception terminalizationFailure)
            {
                await display.WaitForCleanupAsync(CancellationToken.None);
                throw new AggregateException(
                    "Pipeline execution and run terminalization both failed.",
                    activeFailure,
                    terminalizationFailure
                );
            }
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
}
