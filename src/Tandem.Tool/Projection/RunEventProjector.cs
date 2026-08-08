using System.Text.Json;
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
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);

    public async Task EmitBlockStartedAsync(CancellationToken ct = default)
    {
        await EmitAsync(EventKinds.BlockStarted, $"Block {blockId} started", ct: ct);
    }

    public async Task EmitBlockCompletedAsync(
        PipelineRunOutcome outcome,
        CancellationToken ct = default
    )
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
            $"Block {outcome.StepId} completed: {outcome.Kind}",
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

    public async Task EmitRunFaultedAsync(string reason, CancellationToken ct = default)
    {
        await EmitAsync(EventKinds.RunFaulted, reason, ct: ct);
    }

    public async Task EmitRunCancelledAsync(string reason, CancellationToken ct = default)
    {
        await EmitAsync(EventKinds.RunCancelled, reason, ct: ct);
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

    public async Task EmitAgentUpdateAsync(AgentUpdate update, CancellationToken ct = default)
    {
        switch (update)
        {
            case AgentUpdate.Reasoning reasoning:
                await EmitAsync(EventKinds.AgentReasoning, reasoning.Value, data: null, ct: ct);
                break;
            case AgentUpdate.Text text when !string.IsNullOrEmpty(text.Value):
                await EmitAsync(EventKinds.AgentText, text.Value, data: null, ct: ct);
                break;
            case AgentUpdate.Usage usage:
                var usageData = JsonSerializer.SerializeToElement(
                    new
                    {
                        inputTokens = usage.InputTokens,
                        outputTokens = usage.OutputTokens,
                        reasoningTokens = usage.ReasoningTokens,
                        model = profile?.Model,
                        contextWindowTokens = profile?.ContextWindowTokens,
                    }
                );
                await EmitAsync(EventKinds.AgentUsage, "usage", usageData, ct: ct);
                break;
            case AgentUpdate.ToolStarted call:
                _toolNames[call.CallId] = call.Name;
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
            case AgentUpdate.ToolCompleted result:
                var toolName = _toolNames.Remove(result.CallId, out var completedToolName)
                    ? completedToolName
                    : null;
                var resultData = JsonSerializer.SerializeToElement(
                    new
                    {
                        callId = result.CallId,
                        name = toolName,
                        success = result.Succeeded,
                        error = result.Error,
                    }
                );
                await EmitAsync(
                    EventKinds.ToolCompleted,
                    result.Succeeded
                        ? $"{toolName ?? "tool"} done"
                        : $"{toolName ?? "tool"} failed: {result.Error ?? "unknown error"}",
                    resultData,
                    ct: ct
                );
                break;
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

    private static string DescribeToolCall(string name, JsonElement arguments)
    {
        if (
            !name.StartsWith("file_access_", StringComparison.Ordinal)
            || arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("path", out var path)
            || path.ValueKind != JsonValueKind.String
        )
        {
            return name;
        }

        return $"{name} {path.GetString()}";
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
