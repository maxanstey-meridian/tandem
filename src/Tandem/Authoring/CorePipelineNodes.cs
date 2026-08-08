namespace Tandem;

internal interface IPipelineExecutionContext;

internal static class CorePipelineNodes
{
    public static PipelineNodeDescriptor Stage<TInput, TOutput>(
        string id,
        Func<TInput, IPipelineExecutionContext, CancellationToken, ValueTask<TOutput>> execute,
        string? observationId = null,
        PipelineObservationMode observationMode = PipelineObservationMode.Full
    ) =>
        new DelegatePipelineNodeDescriptor<TInput, TOutput>(
            id,
            execute,
            observationId,
            observationMode
        );

    public static PipelineNodeDescriptor RequestPort<TRequest, TResponse>(string id) =>
        new RequestPortPipelineNodeDescriptor<TRequest, TResponse>(id);
}
