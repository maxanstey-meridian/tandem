namespace Tandem.Tool;

internal sealed class CompositePipelineObserver(
    IPipelinePersistenceObserver persistenceObserver,
    params IPipelineObserver[] observers
) : IPipelinePersistenceObserver
{
    public async ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        await persistenceObserver.ObserveAsync(observation, cancellationToken);
        foreach (var observer in observers)
        {
            await observer.ObserveAsync(observation, cancellationToken);
        }
    }
}
