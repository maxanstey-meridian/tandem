using System.Text.Json;
using Tandem.Domain;

namespace Tandem;

public sealed class PipelineRequest<TState, TRequest, TResponse>
{
    internal PipelineRequest(
        string requestStepId,
        string portId,
        string resumeStepId,
        Func<TState, TRequest> createRequest,
        Func<TState, TResponse, TState> applyResponse,
        IBlockExecutionObserver? observer
    )
    {
        Request = new RequestStage(requestStepId, portId, createRequest, observer);
        Port = new RequestPort(portId);
        Resume = new ResumeStage(resumeStepId, portId, applyResponse, observer);
    }

    public IRawPipelineNode Request { get; }
    public IRawPipelineNode Port { get; }
    public IRawPipelineNode Resume { get; }

    private sealed class RequestStage : IRawPipelineNode
    {
        public RequestStage(
            string id,
            string scope,
            Func<TState, TRequest> createRequest,
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
                    return createRequest(pipeline.State);
                },
                observer
            );
        }

        public string Id { get; }
        public PipelineNodeDescriptor Descriptor { get; }
    }

    private sealed class RequestPort(string id) : IRawPipelineNode
    {
        public string Id => id;
        public PipelineNodeDescriptor Descriptor { get; } =
            PipelineNodes.RequestPort<TRequest, TResponse>(id);
    }

    private sealed class ResumeStage : IRawPipelineNode
    {
        public ResumeStage(
            string id,
            string scope,
            Func<TState, TResponse, TState> applyResponse,
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
                    return pipeline with
                    {
                        State = applyResponse(pipeline.State, response),
                        LatestOutcome = new BlockOutcome(
                            "request.resumed",
                            id,
                            $"Request '{scope}' resumed.",
                            JsonSerializer.SerializeToElement(response)
                        ),
                        LatestResult = null,
                    };
                },
                observer
            );
        }

        public string Id { get; }
        public PipelineNodeDescriptor Descriptor { get; }
    }
}
