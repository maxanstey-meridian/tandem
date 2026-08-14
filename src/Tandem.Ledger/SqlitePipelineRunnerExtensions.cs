namespace Tandem.Ledger;

public sealed record SqlitePipelineRunOptions(
    string LedgerPath,
    Guid? RunId = null,
    PipelineInteractionHandlers? Interactions = null,
    IPipelineObserver? Observer = null
);

public static class SqlitePipelineRunnerExtensions
{
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
        var persistenceObserver = await store.CreateObserverAsync(
            runId,
            pipeline,
            cancellationToken
        );
        IPipelineObserver observer = options.Observer is null
            ? persistenceObserver
            : new CompositePersistenceObserver(persistenceObserver, options.Observer);
        LedgerRunStatus? terminalStatus = null;
        var preserveActiveFailure = false;

        try
        {
            var result = await runner.RunAsync(
                pipeline,
                initialState,
                new PipelineRunOptions(runId, options.Interactions, observer)
                {
                    Ledger = store.ForRun(runId),
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
        catch (OperationCanceledException)
        {
            terminalStatus = LedgerRunStatus.Cancelled;
            preserveActiveFailure = true;
            throw;
        }
        catch
        {
            terminalStatus = LedgerRunStatus.Faulted;
            preserveActiveFailure = true;
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
                catch when (preserveActiveFailure)
                {
                    // Preserve the execution failure when best-effort terminalization also fails.
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
