namespace Tandem;

public static class PipelineObservers
{
    /// <summary>
    /// Composes observers that see every observation in the given order; null entries are
    /// skipped. The result is an <see cref="IPipelinePersistenceObserver"/> when the first
    /// observer is one, so persistence records each observation before the others see it.
    /// Returns null when no observer is given.
    /// </summary>
    public static IPipelineObserver? Compose(params IReadOnlyList<IPipelineObserver?> observers)
    {
        IPipelineObserver[] present = [.. observers.OfType<IPipelineObserver>()];
        return present switch
        {
            [] => null,
            [var single] => single,
            [IPipelinePersistenceObserver, ..] => new PersistenceFirst(present),
            _ => new Sequential(present),
        };
    }

    private class Sequential(IReadOnlyList<IPipelineObserver> observers) : IPipelineObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            foreach (var observer in observers)
            {
                await observer.ObserveAsync(observation, cancellationToken);
            }
        }
    }

    private sealed class PersistenceFirst(IReadOnlyList<IPipelineObserver> observers)
        : Sequential(observers),
            IPipelinePersistenceObserver;
}
