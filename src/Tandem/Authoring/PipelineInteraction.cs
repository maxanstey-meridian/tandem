using System.Text.Json;
using Tandem.Domain;

namespace Tandem;

public sealed class PipelineInteraction<TState, TRequest, TResponse>
    : IPipelineInteractionDefinition
{
    internal PipelineInteraction(
        string id,
        Func<TState, TRequest> createRequest,
        Func<TState, TResponse, TState> applyResponse
    )
    {
        Id = id;
        Request = new RequestStage($"{id}--request", id, createRequest);
        Port = new RequestPort(id);
        Resume = new ResumeStage($"{id}--resume", id, applyResponse);
    }

    public string Id { get; }
    internal IRawPipelineNode Request { get; }
    internal IRawPipelineNode Port { get; }
    internal IRawPipelineNode Resume { get; }
    Type IPipelineInteractionDefinition.RequestType => typeof(TRequest);
    Type IPipelineInteractionDefinition.ResponseType => typeof(TResponse);

    private sealed class RequestStage : IRawPipelineNode
    {
        public RequestStage(string id, string scope, Func<TState, TRequest> createRequest)
        {
            Id = id;
            Descriptor = CorePipelineNodes.Stage<
                PipelineMessage<TState>,
                InteractionRequest<TState, TRequest, TResponse>
            >(
                id,
                (pipeline, _, _) =>
                    ValueTask.FromResult(
                        new InteractionRequest<TState, TRequest, TResponse>(
                            scope,
                            pipeline,
                            createRequest(pipeline.State)
                        )
                    ),
                scope,
                PipelineObservationMode.StartOnly
            );
        }

        public string Id { get; }
        public PipelineNodeDescriptor Descriptor { get; }
    }

    private sealed class RequestPort(string id) : IRawPipelineNode
    {
        public string Id => id;
        public PipelineNodeDescriptor Descriptor { get; } =
            CorePipelineNodes.RequestPort<
                InteractionRequest<TState, TRequest, TResponse>,
                InteractionResponse<TState, TResponse>
            >(id);
    }

    private sealed class ResumeStage : IRawPipelineNode
    {
        public ResumeStage(string id, string scope, Func<TState, TResponse, TState> applyResponse)
        {
            Id = id;
            Descriptor = CorePipelineNodes.Stage<
                InteractionResponse<TState, TResponse>,
                PipelineMessage<TState>
            >(
                id,
                (response, _, _) =>
                {
                    if (!string.Equals(response.InteractionId, scope, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Response for interaction '{response.InteractionId}' cannot resume '{scope}'."
                        );
                    }
                    var pipeline = response.Pipeline;
                    return ValueTask.FromResult(
                        pipeline with
                        {
                            State = applyResponse(pipeline.State, response.Response),
                            LatestOutcome = new BlockOutcome(
                                "request.resumed",
                                scope,
                                $"Request '{scope}' resumed.",
                                System.Text.Json.JsonSerializer.SerializeToElement(
                                    response.Response,
                                    TandemJson.TypedContract
                                )
                            ),
                            LatestResult = null,
                        }
                    );
                },
                scope,
                PipelineObservationMode.CompleteOnly
            );
        }

        public string Id { get; }
        public PipelineNodeDescriptor Descriptor { get; }
    }
}

internal interface IPipelineInteractionDefinition
{
    public string Id { get; }
    internal Type RequestType { get; }
    internal Type ResponseType { get; }
}

internal interface IInteractionRequest
{
    public string InteractionId { get; }
    public Type RequestType { get; }
    public Type ResponseType { get; }
    public object Request { get; }
    public object CreateResponse(object response);
    public PipelineObservation CreateRequestedObservation(
        Guid runId,
        string requestId,
        bool persist
    );
    public PipelineObservation CreateAnsweredObservation(
        Guid runId,
        string requestId,
        object response,
        bool persist
    );
}

internal sealed record InteractionRequest<TState, TRequest, TResponse>(
    string InteractionId,
    PipelineMessage<TState> Pipeline,
    TRequest Value
) : IInteractionRequest, IPipelineRunContextCarrier
{
    public PipelineRunContext? RunContext => Pipeline.RunContext;
    Type IInteractionRequest.RequestType => typeof(TRequest);
    Type IInteractionRequest.ResponseType => typeof(TResponse);
    object IInteractionRequest.Request => Value!;

    object IInteractionRequest.CreateResponse(object response) =>
        response is TResponse typed
            ? new InteractionResponse<TState, TResponse>(InteractionId, Pipeline, typed)
            : throw new InvalidOperationException(
                $"Interaction '{InteractionId}' requires response type '{typeof(TResponse).FullName}', "
                    + $"not '{response.GetType().FullName}'."
            );

    PipelineObservation IInteractionRequest.CreateRequestedObservation(
        Guid runId,
        string requestId,
        bool persist
    ) =>
        new PipelineInteractionRequested<TRequest>(
            runId,
            InteractionId,
            requestId,
            Value,
            persist ? JsonSerializer.SerializeToElement(Value, TandemJson.TypedContract) : null
        );

    PipelineObservation IInteractionRequest.CreateAnsweredObservation(
        Guid runId,
        string requestId,
        object response,
        bool persist
    ) =>
        response is TResponse typed
            ? new PipelineInteractionAnswered<TResponse>(
                runId,
                InteractionId,
                requestId,
                typed,
                persist ? JsonSerializer.SerializeToElement(typed, TandemJson.TypedContract) : null
            )
            : throw new InvalidOperationException(
                $"Interaction '{InteractionId}' cannot observe response type "
                    + $"'{response.GetType().FullName}'."
            );
}

internal sealed record InteractionResponse<TState, TResponse>(
    string InteractionId,
    PipelineMessage<TState> Pipeline,
    TResponse Response
) : IPipelineRunContextCarrier
{
    public PipelineRunContext? RunContext => Pipeline.RunContext;
}
