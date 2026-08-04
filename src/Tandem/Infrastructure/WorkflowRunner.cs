using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure;

public sealed class WorkflowRunner
{
    public async Task<BlockResult> RunAsync(
        RunContext context,
        string apiKey,
        Func<WorkflowEvent, Task> onEvent,
        CancellationToken cancellationToken
    )
    {
        var executor = new ImplementationBlockExecutor(apiKey);
        var binding = executor.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();

        var sessionId = context.RunId.ToString("N");
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            context,
            sessionId,
            cancellationToken
        );

        BlockResult? result = null;
        Exception? failure = null;

        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            await onEvent(evt);

            if (evt is WorkflowErrorEvent errorEvent)
            {
                failure = errorEvent.Exception;
            }
            else if (evt is ExecutorFailedEvent failedEvent)
            {
                failure = failedEvent.Data;
            }
            else if (evt is WorkflowOutputEvent output && output.Is<BlockResult>())
            {
                result = output.As<BlockResult>();
            }
        }

        if (failure is not null)
        {
            throw new WorkflowRunException("Workflow execution failed.", failure);
        }

        if (result is null)
        {
            throw new WorkflowRunException("Workflow completed without producing a block result.");
        }

        return result;
    }
}

public sealed class WorkflowRunException(string message, Exception? inner = null)
    : Exception(message, inner);
