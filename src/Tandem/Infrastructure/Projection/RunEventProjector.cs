using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Infrastructure.Projection;

public sealed class RunEventProjector(
    Guid runId,
    string blockId,
    EventStore eventStore,
    Action<RunEvent>? onEvent = null,
    ResolvedProfile? profile = null
)
{
    public async Task EmitBlockStartedAsync(CancellationToken ct = default)
    {
        await EmitAsync(EventKinds.BlockStarted, $"Block {blockId} started", ct: ct);
    }

    public async Task EmitBlockCompletedAsync(BlockOutcome outcome, CancellationToken ct = default)
    {
        var dataValues = new Dictionary<string, object?>
        {
            ["kind"] = outcome.Kind,
            ["summary"] = outcome.Summary,
            ["duration"] = outcome.Duration.TotalMilliseconds,
        };
        if (
            outcome.Payload.ValueKind == JsonValueKind.Object
            && outcome.Payload.TryGetProperty("exitCode", out var exitCode)
        )
        {
            dataValues["exitCode"] = exitCode.GetInt32();
        }
        var data = JsonSerializer.SerializeToElement(dataValues);
        await EmitAsync(
            EventKinds.BlockCompleted,
            $"Block {outcome.BlockId} completed: {outcome.Kind}",
            data,
            ct: ct
        );
    }

    public async Task EmitRunStartedAsync(string packetPath, CancellationToken ct = default)
    {
        await EmitAsync(EventKinds.RunStarted, $"Run {runId:N} started from {packetPath}", ct: ct);
    }

    public async Task EmitRunReadyAsync(string? candidateSha, CancellationToken ct = default)
    {
        await EmitAsync(
            EventKinds.RunReady,
            $"Run ready, candidate: {candidateSha ?? "(none)"}",
            ct: ct
        );
    }

    public async Task EmitRunFailedAsync(string reason, CancellationToken ct = default)
    {
        await EmitAsync(EventKinds.RunFailed, reason, ct: ct);
    }

    public async Task EmitHumanRequestedAsync(
        HumanQuestion question,
        CancellationToken ct = default
    )
    {
        var data = JsonSerializer.SerializeToElement(
            new
            {
                sourceBlockId = question.SourceBlockId,
                question = question.Question,
                reason = question.Reason,
            }
        );
        await EmitAsync(
            EventKinds.HumanRequested,
            $"Human input requested from {question.SourceBlockId}: {question.Question}",
            data,
            ct: ct
        );
    }

    public async Task EmitHumanAnsweredAsync(
        string sourceBlockId,
        string answerText,
        CancellationToken ct = default
    )
    {
        await EmitAsync(
            EventKinds.HumanAnswered,
            $"Human answer from {sourceBlockId}: {answerText}",
            ct: ct
        );
    }

    public async Task EmitCommandOutputAsync(
        string command,
        string output,
        int exitCode,
        CancellationToken ct = default
    )
    {
        var data = JsonSerializer.SerializeToElement(new { command, exitCode });
        await EmitAsync(
            EventKinds.CommandOutput,
            $"[{(exitCode == 0 ? "PASS" : "FAIL")}] {command}\n{output}",
            data,
            ct: ct
        );
    }

    public async Task EmitRunPublishedAsync(
        string branchName,
        string commitSha,
        CancellationToken ct = default
    )
    {
        await EmitAsync(
            EventKinds.RunPublished,
            $"Published: {branchName}\nCommit: {commitSha}",
            ct: ct
        );
    }

    public async Task EmitAgentUpdateAsync(
        AgentResponseUpdate update,
        CancellationToken ct = default
    )
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning:
                    await EmitAsync(EventKinds.AgentReasoning, reasoning.Text, data: null, ct: ct);
                    break;
                case TextContent text:
                    if (!string.IsNullOrEmpty(text.Text))
                    {
                        await EmitAsync(EventKinds.AgentText, text.Text, data: null, ct: ct);
                    }
                    break;
                case UsageContent usage:
                    var usageData = JsonSerializer.SerializeToElement(
                        new
                        {
                            inputTokens = usage.Details.InputTokenCount,
                            outputTokens = usage.Details.OutputTokenCount,
                            reasoningTokens = usage.Details.ReasoningTokenCount,
                            model = profile?.Model,
                            contextWindowTokens = profile?.ContextWindowTokens,
                        }
                    );
                    await EmitAsync(EventKinds.AgentUsage, "usage", usageData, ct: ct);
                    break;
                case FunctionCallContent call:
                    var description = DescribeToolCall(call.Name, call.Arguments);
                    var callData = JsonSerializer.SerializeToElement(
                        new
                        {
                            callId = call.CallId,
                            name = call.Name,
                            arguments = call.Arguments,
                        }
                    );
                    await EmitAsync(EventKinds.ToolStarted, description, callData, ct: ct);
                    break;
                case FunctionResultContent result:
                    var success = result.Exception is null;
                    var resultData = JsonSerializer.SerializeToElement(
                        new
                        {
                            callId = result.CallId,
                            success,
                            error = result.Exception?.Message,
                        }
                    );
                    await EmitAsync(
                        EventKinds.ToolCompleted,
                        success ? "done" : "failed",
                        resultData,
                        ct: ct
                    );
                    break;
            }
        }
    }

    internal static string DescribeToolCall(string name, IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return name;
        }

        var preferredKey = name switch
        {
            _ when name.StartsWith("file_access_", StringComparison.Ordinal) => "path",
            _ => null,
        };

        if (preferredKey is null || !arguments.TryGetValue(preferredKey, out var value))
        {
            return name;
        }

        var detail = value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String =>
                element.GetString(),
            string text => text,
            _ => value?.ToString(),
        };
        if (string.IsNullOrWhiteSpace(detail))
        {
            return name;
        }

        const int maxLength = 240;
        var normalized = detail.ReplaceLineEndings(" ").Trim();
        var truncated =
            normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
        return $"{name}: {truncated}";
    }

    private async Task EmitAsync(
        string kind,
        string message,
        JsonElement? data = null,
        CancellationToken ct = default
    )
    {
        var evt = await eventStore.AppendProjectedAsync(
            runId,
            blockId,
            kind,
            eventId => new RunEvent(
                eventId,
                DateTimeOffset.UtcNow,
                runId,
                blockId,
                kind,
                message,
                data
            ),
            ct
        );
        onEvent?.Invoke(evt);
    }
}
