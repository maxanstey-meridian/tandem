using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Tests.Composition;

internal static class CompositionRunner
{
    /// <summary>
    /// Runs a workflow in-process with the given input and returns the first
    /// PipelineMessage output. Throws if the workflow fails or produces no output.
    /// </summary>
    public static async Task<PipelineMessage<DeliveryState>> RunAsync(
        Workflow workflow,
        PipelineMessage<DeliveryState> input,
        string sessionId
    )
    {
        await using var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            input,
            sessionId,
            CancellationToken.None
        );

        PipelineMessage<DeliveryState>? output = null;
        Exception? failure = null;

        await foreach (var evt in runHandle.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowErrorEvent errorEvent)
            {
                failure = errorEvent.Exception;
            }
            else if (evt is ExecutorFailedEvent failedEvent)
            {
                failure = failedEvent.Data;
            }
            else if (evt is WorkflowOutputEvent oe && oe.Is<PipelineMessage<DeliveryState>>())
            {
                output = oe.As<PipelineMessage<DeliveryState>>();
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException($"Workflow failed: {failure.Message}", failure);
        }

        output.Should().NotBeNull("the workflow must produce a PipelineMessage output");
        return output!;
    }
}
