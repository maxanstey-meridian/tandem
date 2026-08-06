using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class HumanQuestionBlock()
    : Executor<PipelineMessage<SimpleV1State>, HumanQuestion>(BlockIds.HumanQuestion)
{
    public override async ValueTask<HumanQuestion> HandleAsync(
        PipelineMessage<SimpleV1State> message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var outcome = message.LatestOutcome;
        var sourceBlockId = outcome?.BlockId ?? "unknown";
        var question = ReadString(outcome?.Payload, "humanQuestion") ?? "No question provided.";
        var reason =
            ReadString(outcome?.Payload, "reason")
            ?? ReadString(outcome?.Payload, "rationale")
            ?? "No reason provided.";

        await context.QueueStateUpdateAsync(
            message.Runtime.RunId.ToString("N"),
            JsonSerializer.Serialize(message),
            scopeName: "HumanInput",
            cancellationToken: cancellationToken
        );
        return new HumanQuestion(sourceBlockId, question, reason);
    }

    private static string? ReadString(JsonElement? payload, string name) =>
        payload is { } value
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed class ApplyHumanAnswerBlock()
    : Executor<HumanAnswer, PipelineMessage<SimpleV1State>>(BlockIds.ApplyHumanAnswer)
{
    public override async ValueTask<PipelineMessage<SimpleV1State>> HandleAsync(
        HumanAnswer answer,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var keys = await context.ReadStateKeysAsync("HumanInput", cancellationToken);
        if (keys.Count == 0)
        {
            return Failure("No saved pipeline message found in HumanInput scope.");
        }

        var json = await context.ReadStateAsync<string>(
            keys.First(),
            "HumanInput",
            cancellationToken
        );
        if (string.IsNullOrEmpty(json))
        {
            return Failure("Saved pipeline message was empty.");
        }

        var message = JsonSerializer.Deserialize<PipelineMessage<SimpleV1State>>(json);
        if (message is null)
        {
            return Failure("Failed to deserialize saved pipeline message.");
        }

        return Apply(message, answer);
    }

    internal static PipelineMessage<SimpleV1State> Apply(
        PipelineMessage<SimpleV1State> message,
        HumanAnswer answer
    )
    {
        var sourceBlockId = message.LatestOutcome?.BlockId;
        var state = message.State with
        {
            PlannerDecision = null,
            ReviewerHumanAnswer = sourceBlockId == BlockIds.Reviewer ? answer.Text : null,
            Status = Tandem.Domain.RunStatus.Running,
        };
        return new PipelineMessage<SimpleV1State>(
            message.Runtime,
            state,
            new BlockOutcome(
                "human.answered",
                BlockIds.ApplyHumanAnswer,
                answer.Text,
                JsonSerializer.SerializeToElement(new { answer = answer.Text, sourceBlockId })
            )
        );
    }

    private static PipelineMessage<SimpleV1State> Failure(string summary) =>
        new(
            PipelineRuntime.Create(Guid.Empty),
            SimpleV1State.Create(new Packet("empty", "/tmp", "main", [], [], [], ""), "", "") with
            {
                Status = Tandem.Domain.RunStatus.Failed,
            },
            new BlockOutcome(
                "human.failed",
                BlockIds.ApplyHumanAnswer,
                summary,
                JsonSerializer.SerializeToElement(new { })
            )
        );
}
