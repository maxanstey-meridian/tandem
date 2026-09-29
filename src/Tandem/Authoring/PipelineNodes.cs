using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem;

public interface IPipelineNode
{
    public string Id { get; }

    // Public only because source-generated consumer classes compile in a separate assembly.
    // This is an opaque generated-code SPI, not an authoring extension point.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PipelineNodeDescriptor Descriptor { get; }
}

public interface IPipelineNode<TState> : IPipelineNode;

internal interface IRawPipelineNode : IPipelineNode;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPipelineStep<TResult> : IPipelineNode;

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class PipelineNodeDescriptor
{
    internal abstract ExecutorBinding Bind();
}

public static class PipelineNodes
{
    /// <summary>
    /// Creates the adapter-authoring seam for a dynamically supplied state stage.
    /// Ordinary C# applications should prefer a generated <c>[PipelineStage]</c> class.
    /// </summary>
    public static IGeneratedPipelineStep<TState, GeneratedStepCompletion> Stage<TState>(
        string id,
        Func<TState, CancellationToken, ValueTask<TState>> execute
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(execute);
        return new DynamicStatePipelineStep<TState>(id, execute);
    }

    public static IPipelineNode<TState> Failed<TState>(IPipelineFailure<TState> failure) =>
        new DefinitionFailedNode<TState>(failure);

    public static IPipelineNode<TState> Complete<TState>(IPipelineCompletion<TState> completion) =>
        new DefinitionCompleteNode<TState>(completion);

    public static PipelineInteraction<TState, TRequest, TResponse> WaitFor<
        TState,
        TRequest,
        TResponse
    >(
        string id,
        Func<TState, TRequest> createRequest,
        Func<TState, TResponse, TState> applyResponse
    ) => new(id, createRequest, applyResponse);

    public static PipelineParallel<TState> Parallel<TState>(
        string id,
        Func<TState, TState> clone,
        IReadOnlyList<PipelineBranch<TState>> branches,
        Func<PipelineParallelMerge<TState>, TState> merge
    ) => new(id, clone, branches, merge);

    public static PipelineParallel<TState> Parallel<TState>(
        string id,
        Func<TState, TState> clone,
        IReadOnlyList<PipelineBranch<TState>> branches,
        Func<PipelineParallelMerge<TState>, TState> merge,
        int? max
    ) => new(id, clone, branches, merge, max);
}

internal sealed class DynamicStatePipelineStep<TState>(
    string id,
    Func<TState, CancellationToken, ValueTask<TState>> execute
) : IGeneratedPipelineStep<TState, GeneratedStepCompletion>
{
    public string Id { get; } = id;
    public PipelineNodeDescriptor Descriptor { get; } =
        new GeneratedStateStepDescriptor<TState>(id, execute);
}

public interface IPipelineCompletion<TState>
{
    public string Id { get; }

    public string Summarize(TState state);

    public TState Complete(TState state) => state;
}

public interface IPipelineFailure<TState>
{
    public string Id { get; }

    public string Summarize(TState state);

    public TState Fail(TState state) => state;
}

internal sealed class DefinitionCompleteNode<TState>(IPipelineCompletion<TState> completion)
    : IPipelineNode<TState>
{
    public string Id => completion.Id;

    public PipelineNodeDescriptor Descriptor { get; } =
        new DelegatePipelineNodeDescriptor<PipelineMessage<TState>, PipelineMessage<TState>>(
            completion.Id,
            (message, _) =>
            {
                var stopwatch = Stopwatch.StartNew();
                var state = completion.Complete(message.State);
                stopwatch.Stop();
                return ValueTask.FromResult(
                    new PipelineMessage<TState>(
                        message.Runtime,
                        state,
                        new BlockOutcome(
                            StandardOutcomeKinds.Success,
                            completion.Id,
                            completion.Summarize(state),
                            TandemJson.EmptyObject,
                            stopwatch.Elapsed
                        )
                    )
                );
            }
        );
}

internal sealed class DefinitionFailedNode<TState>(IPipelineFailure<TState> failure)
    : IPipelineNode<TState>
{
    public string Id => failure.Id;

    public PipelineNodeDescriptor Descriptor { get; } =
        new DelegatePipelineNodeDescriptor<PipelineMessage<TState>, PipelineMessage<TState>>(
            failure.Id,
            (message, _) =>
            {
                var stopwatch = Stopwatch.StartNew();
                var state = failure.Fail(message.State);
                stopwatch.Stop();
                return ValueTask.FromResult(
                    message with
                    {
                        State = state,
                        LatestOutcome = new BlockOutcome(
                            StandardOutcomeKinds.Failed,
                            failure.Id,
                            failure.Summarize(state),
                            TandemJson.EmptyObject,
                            stopwatch.Elapsed
                        ),
                        Status = PipelineRunStatus.Failed,
                    }
                );
            }
        );
}

internal sealed class DelegatePipelineNodeDescriptor<TInput, TOutput>(
    string id,
    Func<TInput, CancellationToken, ValueTask<TOutput>> execute,
    string? observationId = null,
    PipelineObservationMode observationMode = PipelineObservationMode.Full
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind()
    {
        var executor = new DelegatePipelineNodeExecutor<TInput, TOutput>(
            id,
            observationId ?? id,
            observationMode,
            execute
        );
        return executor.BindExecutor();
    }
}

internal sealed class RequestPortPipelineNodeDescriptor<TRequest, TResponse>(string id)
    : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() =>
        (ExecutorBinding)RequestPort.Create<TRequest, TResponse>(id);
}

internal sealed class DelegatePipelineNodeExecutor<TInput, TOutput>(
    string id,
    string observationId,
    PipelineObservationMode observationMode,
    Func<TInput, CancellationToken, ValueTask<TOutput>> execute
) : Executor<TInput, TOutput>(id, options: null, declareCrossRunShareable: true)
{
    public override async ValueTask<TOutput> HandleAsync(
        TInput input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    ) =>
        await PipelineObservationPublisher.ExecuteAsync(
            observationId,
            observationMode,
            input,
            () => execute(input, cancellationToken),
            cancellationToken
        );
}
