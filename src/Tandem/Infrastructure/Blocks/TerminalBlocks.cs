using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class CompleteBlock : Executor<PipelineMessage, PipelineMessage>
{
    public CompleteBlock()
        : base(BlockIds.Complete) { }

    public override ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = message.Context with { Status = Domain.RunStatus.Ready };
        sw.Stop();
        return ValueTask.FromResult(
            new PipelineMessage(
                ctx,
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

public sealed class WaitingBlock : Executor<PipelineMessage, PipelineMessage>
{
    public WaitingBlock()
        : base(BlockIds.Waiting) { }

    public override ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = message.Context with { Status = Domain.RunStatus.WaitingForHuman };
        sw.Stop();
        return ValueTask.FromResult(
            new PipelineMessage(
                ctx,
                new BlockOutcome(
                    OutcomeKinds.RunWaiting,
                    BlockIds.Waiting,
                    "Waiting for human",
                    JsonSerializer.SerializeToElement(new { }),
                    sw.Elapsed
                )
            )
        );
    }
}

public sealed class FailedBlock : Executor<PipelineMessage, PipelineMessage>
{
    public FailedBlock()
        : base(BlockIds.Failed) { }

    public override ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var sourceBlock = message.LatestOutcome?.BlockId ?? "unknown";
        var sourceKind = message.LatestOutcome?.Kind ?? "unknown";
        var ctx = message.Context with { Status = Domain.RunStatus.Failed };
        sw.Stop();
        return ValueTask.FromResult(
            new PipelineMessage(
                ctx,
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
