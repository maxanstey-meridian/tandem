using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;
using DomainRunStatus = Tandem.Domain.RunStatus;

namespace Tandem.Infrastructure.Blocks;

public sealed class HumanQuestionBlock()
    : Executor<PipelineMessage, HumanQuestion>(BlockIds.HumanQuestion)
{
    public override async ValueTask<HumanQuestion> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();
        var ctx = message.Context;
        var outcome = message.LatestOutcome;

        var sourceBlockId = outcome?.BlockId ?? "unknown";
        var question = "No question provided.";
        var reason = "No reason provided.";

        if (outcome?.Payload is { } payload)
        {
            if (
                payload.TryGetProperty("humanQuestion", out var q)
                && q.ValueKind == JsonValueKind.String
            )
            {
                question = q.GetString() ?? question;
            }
            if (payload.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                reason = r.GetString() ?? reason;
            }
            if (
                payload.TryGetProperty("rationale", out var rat)
                && rat.ValueKind == JsonValueKind.String
            )
            {
                reason = rat.GetString() ?? reason;
            }
        }

        // Preserve the pipeline message in workflow shared state so the
        // apply-human-answer block can restore it after the request port
        // returns the human answer.
        var stateKey = ctx.RunId.ToString("N");
        var pipelineJson = JsonSerializer.Serialize(message);
        await context.QueueStateUpdateAsync(
            stateKey,
            pipelineJson,
            scopeName: "HumanInput",
            cancellationToken: cancellationToken
        );

        sw.Stop();
        return new HumanQuestion(sourceBlockId, question, reason);
    }
}

public sealed class ApplyHumanAnswerBlock()
    : Executor<HumanAnswer, PipelineMessage>(BlockIds.ApplyHumanAnswer)
{
    public override async ValueTask<PipelineMessage> HandleAsync(
        HumanAnswer answer,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var sw = Stopwatch.StartNew();

        // The run ID was used as the state key. We need to find it by reading
        // all state keys in the HumanInput scope. But QueueStateUpdateAsync
        // uses a single key per scope entry, and we set it to the run ID.
        // Read it back. The key is the run ID, but we don't have the run ID
        // directly — the answer doesn't carry it. We read all state keys.
        var keys = await context.ReadStateKeysAsync("HumanInput", cancellationToken);

        if (keys.Count == 0)
        {
            sw.Stop();
            return new PipelineMessage(
                PipelineContext.Create(Guid.Empty, MakeEmptyPacket(), "", "") with
                {
                    Status = DomainRunStatus.Failed,
                },
                new BlockOutcome(
                    "human.failed",
                    BlockIds.ApplyHumanAnswer,
                    "No saved pipeline message found in HumanInput scope.",
                    JsonSerializer.SerializeToElement(new { }),
                    sw.Elapsed
                )
            );
        }

        var stateKey = keys.First();
        var pipelineJson = await context.ReadStateAsync<string>(
            stateKey,
            "HumanInput",
            cancellationToken
        );

        if (string.IsNullOrEmpty(pipelineJson))
        {
            sw.Stop();
            return new PipelineMessage(
                PipelineContext.Create(Guid.Empty, MakeEmptyPacket(), "", "") with
                {
                    Status = DomainRunStatus.Failed,
                },
                new BlockOutcome(
                    "human.failed",
                    BlockIds.ApplyHumanAnswer,
                    "Saved pipeline message was empty.",
                    JsonSerializer.SerializeToElement(new { }),
                    sw.Elapsed
                )
            );
        }

        var message = JsonSerializer.Deserialize<PipelineMessage>(pipelineJson);
        if (message is null)
        {
            sw.Stop();
            return new PipelineMessage(
                PipelineContext.Create(Guid.Empty, MakeEmptyPacket(), "", "") with
                {
                    Status = DomainRunStatus.Failed,
                },
                new BlockOutcome(
                    "human.failed",
                    BlockIds.ApplyHumanAnswer,
                    "Failed to deserialize saved pipeline message.",
                    JsonSerializer.SerializeToElement(new { }),
                    sw.Elapsed
                )
            );
        }

        // Add the human answer to the context. The source block will receive
        // this updated context and the answer text in its next prompt.
        var ctx = message.Context with
        {
            PlannerDecision = null,
            Status = DomainRunStatus.Running,
        };

        sw.Stop();

        return new PipelineMessage(
            ctx,
            new BlockOutcome(
                "human.answered",
                BlockIds.ApplyHumanAnswer,
                answer.Text,
                JsonSerializer.SerializeToElement(
                    new { answer = answer.Text, sourceBlockId = message.LatestOutcome?.BlockId }
                ),
                sw.Elapsed
            )
        );
    }

    private static Packet MakeEmptyPacket() => new("empty", "/tmp", "main", [], [], [], "");
}
