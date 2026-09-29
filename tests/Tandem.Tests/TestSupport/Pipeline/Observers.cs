namespace Tandem.Tests;

internal sealed class RecordingObserver(List<PipelineObservation> observations)
    : IPipelinePersistenceObserver
{
    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        lock (observations)
        {
            observations.Add(observation);
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class InlineObserver(Action<PipelineObservation> observe) : IPipelineObserver
{
    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        observe(observation);
        return ValueTask.CompletedTask;
    }
}
