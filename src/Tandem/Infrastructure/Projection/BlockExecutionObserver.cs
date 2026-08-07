using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure.Projection;

internal sealed class ObservedExecutor<TInput, TOutput>(
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

        await observer.CompletedAsync(Id, input, output, stopwatch.Elapsed, cancellationToken);
        return output;
    }
}

public sealed class RunEventBlockExecutionObserver(Func<string, RunEventProjector> projectorFactory)
    : IBlockExecutionObserver,
        ICommandOutputObserver
{
    public async ValueTask StartedAsync(string blockId, CancellationToken cancellationToken) =>
        await projectorFactory(blockId).EmitBlockStartedAsync(cancellationToken);

    public async ValueTask CompletedAsync<TInput, TOutput>(
        string blockId,
        TInput input,
        TOutput output,
        TimeSpan duration,
        CancellationToken cancellationToken
    )
    {
        if (input is HumanAnswer answer && output is IOutcomeBearingMessage answeredMessage)
        {
            var sourceBlockId = ReadString(answeredMessage.LatestOutcome?.Payload, "sourceBlockId");
            await projectorFactory(blockId)
                .EmitHumanAnsweredAsync(sourceBlockId ?? "unknown", answer.Text, cancellationToken);
        }

        var outcome = output is IOutcomeBearingMessage message ? message.LatestOutcome : null;
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

    private static string? ReadString(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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
