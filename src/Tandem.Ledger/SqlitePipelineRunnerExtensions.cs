namespace Tandem.Ledger;

public sealed record SqlitePipelineRunOptions(
    string LedgerPath,
    Guid? RunId = null,
    PipelineInteractionHandlers? Interactions = null,
    IPipelineObserver? Observer = null
)
{
    public bool EnableLedgerTools { get; init; }
}

public static class SqlitePipelineRunnerExtensions
{
    public static PipelineRunOptions WithRunLedger(
        this PipelineRunOptions options,
        RunLedger ledger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ledger);
        return options with { Ledger = ledger };
    }

    public static async Task<PipelineRunResult<TState>> RunAsync<TState>(
        this PipelineRunner runner,
        Pipeline<TState> pipeline,
        TState initialState,
        SqlitePipelineRunOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LedgerPath);

        var runId = options.RunId ?? Guid.CreateVersion7();
        var store = new SqliteLedgerStore(options.LedgerPath);
        LedgerRunStatus? terminalStatus = null;
        var preserveActiveFailure = false;
        Exception? activeFailure = null;
        try
        {
            // Run-row creation is bookkeeping, not pipeline work: once RunAsync is entered
            // the row must exist so terminalization always finds the run, even when
            // cancellation fires during startup.
            var persistenceObserver = await store.CreateObserverAsync(runId, pipeline);
            IPipelineObserver observer = options.Observer is null
                ? persistenceObserver
                : new CompositePersistenceObserver(persistenceObserver, options.Observer);

            var result = await runner.RunAsync(
                pipeline,
                initialState,
                new PipelineRunOptions(runId, options.Interactions, observer)
                {
                    Ledger = options.EnableLedgerTools ? store.ForRun(runId) : null,
                },
                cancellationToken
            );
            terminalStatus = result.Status switch
            {
                PipelineRunStatus.Succeeded => LedgerRunStatus.Ready,
                PipelineRunStatus.Failed => LedgerRunStatus.Failed,
                _ => LedgerRunStatus.Faulted,
            };
            return result;
        }
        catch (OperationCanceledException exception)
        {
            terminalStatus = LedgerRunStatus.Cancelled;
            preserveActiveFailure = true;
            activeFailure = exception;
            throw;
        }
        catch (Exception exception)
        {
            terminalStatus = LedgerRunStatus.Faulted;
            preserveActiveFailure = true;
            activeFailure = exception;
            throw;
        }
        finally
        {
            if (terminalStatus is { } status)
            {
                try
                {
                    await store.CompleteRunAsync(runId, status, CancellationToken.None);
                }
                catch (Exception terminalizationFailure) when (preserveActiveFailure)
                {
                    throw new AggregateException(
                        "Pipeline execution and ledger terminalization both failed.",
                        activeFailure!,
                        terminalizationFailure
                    );
                }
            }
        }
    }

    private sealed class CompositePersistenceObserver(
        IPipelinePersistenceObserver persistenceObserver,
        IPipelineObserver observer
    ) : IPipelinePersistenceObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            await persistenceObserver.ObserveAsync(observation, cancellationToken);
            await observer.ObserveAsync(observation, cancellationToken);
        }
    }
}
