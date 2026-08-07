using System.Diagnostics;
using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class CompleteBlock
{
    public ValueTask<PipelineMessage<DeliveryState>> ExecuteAsync(
        PipelineMessage<DeliveryState> message
    )
    {
        var sw = Stopwatch.StartNew();
        var state = message.State with { Status = Domain.RunStatus.Ready };
        sw.Stop();
        return ValueTask.FromResult(
            new PipelineMessage<DeliveryState>(
                message.Runtime,
                state,
                new BlockOutcome(
                    OutcomeKinds.RunReady,
                    BlockIds.Complete,
                    "Run ready",
                    JsonSerializer.SerializeToElement(new { }),
                    sw.Elapsed
                )
            )
        );
    }
}

public sealed class FailedBlock
{
    public ValueTask<PipelineMessage<DeliveryState>> ExecuteAsync(
        PipelineMessage<DeliveryState> message
    )
    {
        var sw = Stopwatch.StartNew();
        var sourceBlock = message.LatestOutcome?.BlockId ?? "unknown";
        var sourceKind = message.LatestOutcome?.Kind ?? "unknown";
        var state = message.State with { Status = Domain.RunStatus.Failed };
        sw.Stop();
        return ValueTask.FromResult(
            new PipelineMessage<DeliveryState>(
                message.Runtime,
                state,
                new BlockOutcome(
                    OutcomeKinds.RunFailed,
                    BlockIds.Failed,
                    $"Unhandled outcome '{sourceKind}' from block '{sourceBlock}'",
                    JsonSerializer.SerializeToElement(new { }),
                    sw.Elapsed
                )
            )
        );
    }
}
