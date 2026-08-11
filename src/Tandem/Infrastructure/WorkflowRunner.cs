using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Tandem.Domain;

namespace Tandem.Infrastructure;

internal sealed record PendingExternalRequest(
    Guid RunId,
    string RequestId,
    string PortId,
    Type RequestType,
    Type ResponseType,
    object Value
);

internal sealed record ExternalRequestAnswer(
    Guid RunId,
    string RequestId,
    Type ResponseType,
    object Value
);

internal interface IExternalRequestHandler
{
    public ValueTask<ExternalRequestAnswer> WaitAsync(
        PendingExternalRequest request,
        CancellationToken cancellationToken
    );
}

internal sealed class InProcessPipelineRunner
{
    public Task<PipelineMessage<TState>> RunAsync<TState>(
        Pipeline<TState> pipeline,
        Guid runId,
        TState initialState,
        CancellationToken cancellationToken
    ) =>
        RunAsync(
            pipeline,
            runId,
            initialState,
            RejectExternalRequests.Instance,
            observer: null,
            unitOfWork: null,
            cancellationToken
        );

    public Task<PipelineMessage<TState>> RunAsync<TState>(
        Pipeline<TState> pipeline,
        Guid runId,
        TState initialState,
        IPipelineObserver? observer,
        CancellationToken cancellationToken
    ) =>
        RunAsync(
            pipeline,
            runId,
            initialState,
            RejectExternalRequests.Instance,
            observer,
            unitOfWork: null,
            cancellationToken
        );

    public Task<PipelineMessage<TState>> RunAsync<TState>(
        Pipeline<TState> pipeline,
        Guid runId,
        TState initialState,
        IPipelineObserver? observer,
        IPipelineAcceptanceUnitOfWork? unitOfWork,
        CancellationToken cancellationToken
    ) =>
        RunAsync(
            pipeline,
            runId,
            initialState,
            RejectExternalRequests.Instance,
            observer,
            unitOfWork,
            cancellationToken
        );

    public async Task<PipelineMessage<TState>> RunAsync<TState>(
        Pipeline<TState> pipeline,
        Guid runId,
        TState initialState,
        IExternalRequestHandler requests,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            pipeline,
            runId,
            initialState,
            requests,
            observer: null,
            unitOfWork: null,
            cancellationToken
        );

    public async Task<PipelineMessage<TState>> RunAsync<TState>(
        Pipeline<TState> pipeline,
        Guid runId,
        TState initialState,
        IExternalRequestHandler requests,
        IPipelineObserver? observer,
        CancellationToken cancellationToken
    ) =>
        await RunAsync(
            pipeline,
            runId,
            initialState,
            requests,
            observer,
            unitOfWork: null,
            cancellationToken
        );

    public async Task<PipelineMessage<TState>> RunAsync<TState>(
        Pipeline<TState> pipeline,
        Guid runId,
        TState initialState,
        IExternalRequestHandler requests,
        IPipelineObserver? observer,
        IPipelineAcceptanceUnitOfWork? unitOfWork,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(requests);

        var initialMessage = new PipelineMessage<TState>(
            PipelineRuntime.Create(runId),
            initialState
        )
        {
            RunContext = new PipelineRunContext(
                runId,
                observer,
                unitOfWork,
                pipeline.PersistentStepIds
            ),
        };
        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(
            PipelineMafBridge.GetWorkflow(pipeline),
            initialMessage,
            runId.ToString("N"),
            cancellationToken
        );

        PipelineMessage<TState>? output = null;
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var pendingResponses = new List<Task>();
        var haltedRequests = new List<ExternalRequest>();
        var handlerFailure = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void DispatchHaltedRequests()
        {
            if (haltedRequests.Count == 0)
            {
                return;
            }
            var responsesMaySend = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var handlersNotStarted = haltedRequests.Count;
            void MarkHandlerStarted()
            {
                if (Interlocked.Decrement(ref handlersNotStarted) == 0)
                {
                    responsesMaySend.SetResult();
                }
            }
            foreach (var haltedRequest in haltedRequests)
            {
                pendingResponses.Add(
                    HandleRequestAsync(
                        run,
                        runId,
                        haltedRequest,
                        requests,
                        handlerFailure,
                        runCancellation,
                        responsesMaySend.Task,
                        MarkHandlerStarted
                    )
                );
            }
            haltedRequests.Clear();
        }

        Exception? failure = null;
        try
        {
            while (true)
            {
                await foreach (
                    var evt in run.WatchStreamAsync(
                        blockOnPendingRequest: false,
                        runCancellation.Token
                    )
                )
                {
                    switch (evt)
                    {
                        case WorkflowErrorEvent error:
                            throw new PipelineRunException(
                                "Workflow execution failed.",
                                error.Exception
                            );
                        case ExecutorFailedEvent failed:
                            throw new PipelineRunException(
                                "Workflow executor failed.",
                                failed.Data
                            );
                        case WorkflowOutputEvent workflowOutput
                            when workflowOutput.Is<PipelineMessage<TState>>():
                            output = workflowOutput.As<PipelineMessage<TState>>();
                            break;
                        case RequestInfoEvent request:
                            haltedRequests.Add(request.Request);
                            break;
                    }
                }
                if (haltedRequests.Count == 0)
                {
                    break;
                }
                var firstCurrentResponse = pendingResponses.Count;
                DispatchHaltedRequests();
                await Task.WhenAll(pendingResponses.Skip(firstCurrentResponse));
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            if (initialMessage.RunContext is { } runContext)
            {
                await runContext.TerminalizeActiveParallelAsync(
                    cancellationToken.IsCancellationRequested || IsCancellation(failure),
                    failure.Message
                );
            }
            runCancellation.Cancel();
            await CancelQuietlyAsync(run);
        }
        try
        {
            await Task.WhenAll(pendingResponses);
        }
        catch (Exception ex) when (failure is not null)
        {
            _ = ex;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        runCancellation.Cancel();
        if (handlerFailure.Task.IsCompletedSuccessfully)
        {
            failure = await handlerFailure.Task;
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return output
            ?? throw new PipelineRunException(
                "Workflow completed without producing a pipeline output."
            );
    }

    private static bool IsCancellation(Exception exception) =>
        exception is OperationCanceledException
        || (
            exception is AggregateException aggregate
            && aggregate.InnerExceptions.Any(IsCancellation)
        )
        || (exception.InnerException is { } inner && IsCancellation(inner));

    private static async Task HandleRequestAsync(
        StreamingRun run,
        Guid runId,
        ExternalRequest request,
        IExternalRequestHandler requests,
        TaskCompletionSource<Exception> handlerFailure,
        CancellationTokenSource runCancellation,
        Task responsesMaySend,
        Action markHandlerStarted
    )
    {
        try
        {
            await SendResponseAsync(
                run,
                runId,
                request,
                requests,
                responsesMaySend,
                markHandlerStarted,
                runCancellation.Token
            );
        }
        catch (Exception ex)
        {
            handlerFailure.TrySetResult(ex);
            runCancellation.Cancel();
            await CancelQuietlyAsync(run);
            throw;
        }
    }

    private static async ValueTask SendResponseAsync(
        StreamingRun run,
        Guid runId,
        ExternalRequest request,
        IExternalRequestHandler requests,
        Task responsesMaySend,
        Action markHandlerStarted,
        CancellationToken cancellationToken
    )
    {
        var requestType = ResolveType(request.PortInfo.RequestType);
        if (!request.TryGetDataAs(requestType, out var requestData))
        {
            throw new InvalidOperationException(
                $"Request '{request.RequestId}' did not contain the declared request type "
                    + $"'{request.PortInfo.RequestType.TypeName}'."
            );
        }

        var interaction = requestData as IInteractionRequest;
        var interactionRunContext = (interaction as IPipelineRunContextCarrier)?.RunContext;
        var authoredRequestType = interaction?.RequestType ?? requestType;
        var authoredResponseType =
            interaction?.ResponseType ?? ResolveType(request.PortInfo.ResponseType);
        var authoredRequest = interaction?.Request ?? requestData;

        var pending = new PendingExternalRequest(
            runId,
            request.RequestId,
            interaction?.InteractionId ?? request.PortInfo.PortId,
            authoredRequestType,
            authoredResponseType,
            authoredRequest
        );
        try
        {
            if (interactionRunContext is not null)
            {
                await interactionRunContext.ObserveAsync(
                    interaction!.CreateRequestedObservation(
                        runId,
                        request.RequestId,
                        interactionRunContext.ShouldPersist(interaction.InteractionId)
                    ),
                    cancellationToken
                );
            }
            ValueTask<ExternalRequestAnswer> pendingAnswer;
            try
            {
                pendingAnswer = requests.WaitAsync(pending, cancellationToken);
            }
            finally
            {
                markHandlerStarted();
            }
            var answer = await pendingAnswer;
            if (
                answer.RunId != runId
                || !string.Equals(answer.RequestId, request.RequestId, StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    $"Answer for run/request '{answer.RunId:N}/{answer.RequestId}' cannot satisfy "
                        + $"pending run/request '{runId:N}/{request.RequestId}'."
                );
            }

            if (answer.ResponseType != authoredResponseType)
            {
                throw new InvalidOperationException(
                    $"Answer for request '{request.RequestId}' declared response type "
                        + $"'{answer.ResponseType.FullName}', not '{authoredResponseType.FullName}'."
                );
            }
            if (!authoredResponseType.IsInstanceOfType(answer.Value))
            {
                throw new InvalidOperationException(
                    $"Answer for request '{request.RequestId}' produced "
                        + $"'{answer.Value?.GetType().FullName}', not '{authoredResponseType.FullName}'."
                );
            }
            var response = answer.Value;
            if (interactionRunContext is not null)
            {
                await interactionRunContext.ObserveAsync(
                    interaction!.CreateAnsweredObservation(
                        runId,
                        request.RequestId,
                        response,
                        interactionRunContext.ShouldPersist(interaction.InteractionId)
                    ),
                    cancellationToken
                );
            }
            await responsesMaySend.WaitAsync(cancellationToken);
            await run.SendResponseAsync(
                request.CreateResponse(interaction?.CreateResponse(response) ?? response)
            );
        }
        catch (OperationCanceledException)
        {
            if (interactionRunContext is not null)
            {
                await interactionRunContext.ObserveAsync(
                    new PipelineStepCancelled(runId, interaction!.InteractionId),
                    CancellationToken.None
                );
            }
            throw;
        }
        catch (Exception ex)
        {
            if (interactionRunContext is not null)
            {
                await interactionRunContext.ObserveAsync(
                    new PipelineStepFaulted(runId, interaction!.InteractionId, ex.Message),
                    CancellationToken.None
                );
            }
            throw;
        }
    }

    private static Type ResolveType(TypeId typeId) =>
        Type.GetType($"{typeId.TypeName}, {typeId.AssemblyName}", throwOnError: true)!;

    private static async ValueTask CancelQuietlyAsync(StreamingRun run)
    {
        try
        {
            await run.CancelRunAsync();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private sealed class RejectExternalRequests : IExternalRequestHandler
    {
        public static RejectExternalRequests Instance { get; } = new();

        public ValueTask<ExternalRequestAnswer> WaitAsync(
            PendingExternalRequest request,
            CancellationToken cancellationToken
        ) =>
            ValueTask.FromException<ExternalRequestAnswer>(
                new InvalidOperationException(
                    $"Workflow requested external input from port '{request.PortId}', but no "
                        + "external request handler was provided."
                )
            );
    }
}
