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
        // Run-row creation is bookkeeping, not pipeline work: once RunAsync is entered the row
        // must exist so terminalization always finds the run, even when cancellation fires
        // during startup.
        var persistence = await store.CreateObserverAsync(runId, pipeline);
        return await store.RecordRunAsync(
            runId,
            () =>
                runner.RunAsync(
                    pipeline,
                    initialState,
                    new PipelineRunOptions(
                        runId,
                        options.Interactions,
                        PipelineObservers.Compose(persistence, options.Observer)
                    )
                    {
                        Ledger = options.EnableLedgerTools ? store.ForRun(runId) : null,
                    },
                    cancellationToken
                )
        );
    }
}
