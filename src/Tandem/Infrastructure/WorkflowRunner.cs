using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Tandem.Domain;

namespace Tandem.Infrastructure;

internal sealed record PendingExternalRequest(
    Guid RunId,
    string RequestId,
    string PortId,
    string RequestType,
    string ResponseType,
    JsonElement Payload
);

internal sealed record ExternalRequestAnswer(Guid RunId, string RequestId, JsonElement Payload);

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
        Pipeline pipeline,
        Guid runId,
        TState initialState,
        CancellationToken cancellationToken
    ) =>
        RunAsync(pipeline, runId, initialState, RejectExternalRequests.Instance, cancellationToken);

    public async Task<PipelineMessage<TState>> RunAsync<TState>(
        Pipeline pipeline,
        Guid runId,
        TState initialState,
        IExternalRequestHandler requests,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(requests);

        var initialMessage = new PipelineMessage<TState>(
            PipelineRuntime.Create(runId),
            initialState
        );
        await using var run = await InProcessExecution.RunStreamingAsync(
            PipelineMafBridge.GetWorkflow(pipeline),
            initialMessage,
            runId.ToString("N"),
            cancellationToken
        );

        PipelineMessage<TState>? output = null;
        try
        {
            await foreach (var evt in run.WatchStreamAsync(cancellationToken))
            {
                switch (evt)
                {
                    case WorkflowErrorEvent error:
                        throw new WorkflowRunException(
                            "Workflow execution failed.",
                            error.Exception
                        );
                    case ExecutorFailedEvent failed:
                        throw new WorkflowRunException("Workflow executor failed.", failed.Data);
                    case WorkflowOutputEvent workflowOutput
                        when workflowOutput.Is<PipelineMessage<TState>>():
                        output = workflowOutput.As<PipelineMessage<TState>>();
                        break;
                    case RequestInfoEvent request:
                        await SendResponseAsync(
                            run,
                            runId,
                            request.Request,
                            requests,
                            cancellationToken
                        );
                        break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await CancelQuietlyAsync(run);
            throw;
        }

        return output
            ?? throw new WorkflowRunException(
                "Workflow completed without producing a pipeline output."
            );
    }

    private static async ValueTask SendResponseAsync(
        StreamingRun run,
        Guid runId,
        ExternalRequest request,
        IExternalRequestHandler requests,
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

        var pending = new PendingExternalRequest(
            runId,
            request.RequestId,
            request.PortInfo.PortId,
            request.PortInfo.RequestType.TypeName,
            request.PortInfo.ResponseType.TypeName,
            JsonSerializer.SerializeToElement(requestData, requestType)
        );
        var answer = await requests.WaitAsync(pending, cancellationToken);
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

        var responseType = ResolveType(request.PortInfo.ResponseType);
        var response =
            JsonSerializer.Deserialize(answer.Payload, responseType)
            ?? throw new InvalidOperationException(
                $"Answer for request '{request.RequestId}' produced a null response."
            );
        await run.SendResponseAsync(request.CreateResponse(response));
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

public sealed class WorkflowRunException(string message, Exception? inner = null)
    : Exception(message, inner);
