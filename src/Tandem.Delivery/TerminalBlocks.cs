using System.Diagnostics;
using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Delivery;

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
            message with
            {
                State = state,
                LatestOutcome = new BlockOutcome(
                    OutcomeKinds.RunFailed,
                    BlockIds.Failed,
                    $"Unhandled outcome '{sourceKind}' from block '{sourceBlock}'",
                    message.LatestOutcome?.Payload ?? JsonSerializer.SerializeToElement(new { }),
                    sw.Elapsed
                ),
                Disposition = PipelineRunDisposition.Failed,
            }
        );
    }
}
