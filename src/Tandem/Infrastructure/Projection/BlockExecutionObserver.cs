using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure.Projection;

public interface IBlockExecutionObserver
{
    public ValueTask StartedAsync(string blockId, CancellationToken cancellationToken);

    public ValueTask CompletedAsync(
        string blockId,
        BlockOutcome? outcome,
        TimeSpan duration,
        CancellationToken cancellationToken
    );
}

public interface ICommandOutputObserver
{
    public ValueTask CommandOutputAsync(
        string blockId,
        string command,
        string output,
        int exitCode,
        CancellationToken cancellationToken
    );
}

public sealed class ObservedExecutor<TInput, TOutput>(
    string blockId,
    Executor<TInput, TOutput> inner,
    IBlockExecutionObserver observer
) : Executor<TInput, TOutput>(blockId)
{
    public override async ValueTask<TOutput> HandleAsync(
        TInput input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        await observer.StartedAsync(Id, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var output = await inner.HandleAsync(input, context, cancellationToken);
        stopwatch.Stop();

        var outcome = output is IOutcomeBearingMessage message ? message.LatestOutcome : null;
        await observer.CompletedAsync(Id, outcome, stopwatch.Elapsed, cancellationToken);
        return output;
    }
}

public sealed class RunEventBlockExecutionObserver(Func<string, RunEventProjector> projectorFactory)
    : IBlockExecutionObserver,
        ICommandOutputObserver
{
    public async ValueTask StartedAsync(string blockId, CancellationToken cancellationToken) =>
        await projectorFactory(blockId).EmitBlockStartedAsync(cancellationToken);

    public async ValueTask CompletedAsync(
        string blockId,
        BlockOutcome? outcome,
        TimeSpan duration,
        CancellationToken cancellationToken
    )
    {
        var completed = outcome is null
            ? new BlockOutcome(
                EventKinds.BlockCompleted,
                blockId,
                $"{blockId} completed",
                JsonSerializer.SerializeToElement(new { }),
                duration
            )
            : outcome with
            {
                Duration = duration,
            };
        await projectorFactory(blockId).EmitBlockCompletedAsync(completed, cancellationToken);
    }

    public async ValueTask CommandOutputAsync(
        string blockId,
        string command,
        string output,
        int exitCode,
        CancellationToken cancellationToken
    ) =>
        await projectorFactory(blockId)
            .EmitCommandOutputAsync(command, output, exitCode, cancellationToken);
}
