using System.Text.Json;
using Tandem.Domain;
using Tandem.Infrastructure.Projection;

namespace Tandem.Infrastructure.Blocks;

internal static class HumanQuestionBlock
{
    public static async ValueTask<HumanQuestion> ExecuteAsync(
        PipelineMessage<DeliveryState> message,
        IPipelineExecutionContext context,
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

public static class ApplyHumanAnswerBlock
{
    public static async ValueTask<PipelineMessage<DeliveryState>> ExecuteAsync(
        HumanAnswer answer,
        IPipelineExecutionContext context,
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

        var message = JsonSerializer.Deserialize<PipelineMessage<DeliveryState>>(json);
        if (message is null)
        {
            return Failure("Failed to deserialize saved pipeline message.");
        }

        return Apply(message, answer);
    }

    public static PipelineMessage<DeliveryState> Apply(
        PipelineMessage<DeliveryState> message,
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
        return new PipelineMessage<DeliveryState>(
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

    private static PipelineMessage<DeliveryState> Failure(string summary) =>
        new(
            PipelineRuntime.Create(Guid.Empty),
            DeliveryState.Create(new Packet("empty", "/tmp", "main", [], [], [], ""), "", "") with
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

public sealed class HumanQuestionStage(IBlockExecutionObserver? observer = null) : IPipelineNode
{
    public string Id => BlockIds.HumanQuestion;

    public PipelineNodeDescriptor Descriptor { get; } =
        PipelineNodes.Stage<PipelineMessage<DeliveryState>, HumanQuestion>(
            BlockIds.HumanQuestion,
            HumanQuestionBlock.ExecuteAsync,
            observer
        );
}

public sealed class HumanInputPort : IPipelineNode
{
    public string Id => "HumanInput";

    public PipelineNodeDescriptor Descriptor { get; } =
        PipelineNodes.RequestPort<HumanQuestion, HumanAnswer>("HumanInput");
}

public sealed class ApplyHumanAnswerStage(IBlockExecutionObserver? observer = null) : IPipelineNode
{
    public string Id => BlockIds.ApplyHumanAnswer;

    public PipelineNodeDescriptor Descriptor { get; } =
        PipelineNodes.Stage<HumanAnswer, PipelineMessage<DeliveryState>>(
            BlockIds.ApplyHumanAnswer,
            ApplyHumanAnswerBlock.ExecuteAsync,
            observer
        );

    internal static PipelineMessage<DeliveryState> Apply(
        PipelineMessage<DeliveryState> message,
        HumanAnswer answer
    ) => ApplyHumanAnswerBlock.Apply(message, answer);
}
