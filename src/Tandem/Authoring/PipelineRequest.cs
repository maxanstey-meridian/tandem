using System.Text.Json;
using Tandem.Domain;

namespace Tandem;

public sealed class PipelineRequest<TState, TRequest, TResponse>
{
    internal PipelineRequest(
        string requestStepId,
        string portId,
        string resumeStepId,
        Func<PipelineMessage<TState>, TRequest> createRequest,
        Func<PipelineMessage<TState>, TResponse, PipelineMessage<TState>> applyResponse,
        IBlockExecutionObserver? observer
    )
    {
        Request = new RequestStage(requestStepId, portId, createRequest, observer);
        Port = new RequestPort(portId);
        Resume = new ResumeStage(resumeStepId, portId, applyResponse, observer);
    }

    public IPipelineNode Request { get; }
    public IPipelineNode Port { get; }
    public IPipelineNode Resume { get; }

    private sealed class RequestStage : IPipelineNode
    {
        public RequestStage(
            string id,
            string scope,
            Func<PipelineMessage<TState>, TRequest> createRequest,
            IBlockExecutionObserver? observer
        )
        {
            Id = id;
            Descriptor = PipelineNodes.Stage<PipelineMessage<TState>, TRequest>(
                id,
                async (pipeline, context, cancellationToken) =>
                {
                    await context.QueueStateUpdateAsync(
                        pipeline.Runtime.RunId.ToString("N"),
                        JsonSerializer.Serialize(pipeline),
                        scope,
                        cancellationToken
                    );
                    return createRequest(pipeline);
                },
                observer
            );
        }

        public string Id { get; }
        public PipelineNodeDescriptor Descriptor { get; }
    }

    private sealed class RequestPort(string id) : IPipelineNode
    {
        public string Id => id;
        public PipelineNodeDescriptor Descriptor { get; } =
            PipelineNodes.RequestPort<TRequest, TResponse>(id);
    }

    private sealed class ResumeStage : IPipelineNode
    {
        public ResumeStage(
            string id,
            string scope,
            Func<PipelineMessage<TState>, TResponse, PipelineMessage<TState>> applyResponse,
            IBlockExecutionObserver? observer
        )
        {
            Id = id;
            Descriptor = PipelineNodes.Stage<TResponse, PipelineMessage<TState>>(
                id,
                async (response, context, cancellationToken) =>
                {
                    var keys = await context.ReadStateKeysAsync(scope, cancellationToken);
                    if (keys.Count != 1)
                    {
                        throw new InvalidOperationException(
                            $"Expected one saved pipeline message for request port '{scope}'."
                        );
                    }

                    var json = await context.ReadStateAsync<string>(
                        keys.Single(),
                        scope,
                        cancellationToken
                    );
                    var pipeline =
                        JsonSerializer.Deserialize<PipelineMessage<TState>>(json ?? "")
                        ?? throw new InvalidOperationException(
                            $"The saved pipeline message for request port '{scope}' was invalid."
                        );
                    return applyResponse(pipeline, response);
                },
                observer
            );
        }

        public string Id { get; }
        public PipelineNodeDescriptor Descriptor { get; }
    }
}
