using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

internal sealed partial class AgentBlock<TState>
{
    private AIAgent ConfigureFunctionInvocation(
        AIAgent agent,
        ToolOutcomeCollector collector,
        PipelineMessage<TState> message,
        CapabilityInvocationState<TState> capabilityInvocation,
        IReadOnlySet<string> boundCapabilityNames,
        ToolEffectRegistry toolEffects,
        string? workingDirectory
    ) =>
        agent
            .AsBuilder()
            .Use(
                (_, context, next, cancellationToken) =>
                    InvokeToolAsync(
                        context,
                        next,
                        collector,
                        message,
                        capabilityInvocation,
                        boundCapabilityNames,
                        toolEffects,
                        workingDirectory,
                        cancellationToken
                    )
            )
            .Build();

    private async ValueTask<object?> InvokeToolAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        ToolOutcomeCollector collector,
        PipelineMessage<TState> message,
        CapabilityInvocationState<TState> capabilityInvocation,
        IReadOnlySet<string> boundCapabilityNames,
        ToolEffectRegistry toolEffects,
        string? workingDirectory,
        CancellationToken ct
    )
    {
        var name = context.Function.Name;
        var reservation = collector.ReserveToolInvocation();
        ToolSemantics? semantics = toolEffects.TryGet(name, out var classified) ? classified : null;
        var effect = semantics?.Effect ?? ToolEffect.Unclassified;
        var actionInvocationId =
            $"{message.Runtime.NextInvocationId(config.StepId)}--action-{reservation.Ordinal + 1}";
        var arguments = TandemJson.EmptyObject;

        async ValueTask RecordAsync(
            ToolInvocationStatus status,
            ToolResultEvidence? evidence,
            CancellationToken token
        )
        {
            var process = evidence as ToolResultEvidence.Process;
            collector.CompleteToolInvocation(
                reservation,
                new ToolInvocationObservation(
                    name,
                    effect,
                    arguments,
                    status,
                    process is null
                        ? evidence
                        : process with
                        {
                            Stdout = DiagnosticPreview(process.Stdout),
                            Stderr = DiagnosticPreview(process.Stderr),
                            Truncated = process.Truncated || IsPreviewTruncated(process),
                        }
                )
            );
            await ObserveAsync(
                message,
                new PipelineActionCompleted(
                    message.Runtime.RunId,
                    config.StepId,
                    actionInvocationId,
                    name,
                    effect,
                    status,
                    process is null
                        ? null
                        : new PipelineActionProcessPayload(
                            arguments,
                            process.ExitCode,
                            process.Stdout,
                            process.Stderr,
                            process.Duration,
                            process.TimedOut,
                            process.Truncated
                        )
                ),
                token
            );
        }

        async ValueTask FinishAsync(ToolInvocationStatus status, string? updateText)
        {
            var token = status == ToolInvocationStatus.Faulted ? CancellationToken.None : ct;
            await RecordAsync(status, null, token);
            collector.RecordFailedToolCall(reservation, name);
            if (updateText is not null)
            {
                await PublishUpdateAsync(
                    message,
                    new AgentUpdate.ToolCompleted(actionInvocationId, null, updateText),
                    token
                );
            }
        }

        await ObserveAsync(
            message,
            new PipelineActionAttempted(
                message.Runtime.RunId,
                config.StepId,
                actionInvocationId,
                name,
                effect
            ),
            ct
        );

        try
        {
            arguments = JsonSerializer.SerializeToElement(
                context.Arguments,
                TandemJson.TypedContract
            );
        }
        catch
        {
            await FinishAsync(ToolInvocationStatus.Faulted, null);
            throw;
        }
        await PublishUpdateAsync(
            message,
            new AgentUpdate.ToolStarted(actionInvocationId, name, arguments)
            {
                WorkingDirectory = workingDirectory,
            },
            ct
        );

        var gate = ResolveActiveGates(message)
            .FirstOrDefault(active =>
                (effect == ToolEffect.Unclassified || active.BlockedEffects.Contains(effect))
                && !string.Equals(active.ReleaseCapabilityName, name, StringComparison.Ordinal)
            );
        if (gate is not null)
        {
            await FinishAsync(ToolInvocationStatus.Blocked, "Action blocked by gate.");
            return new ToolError(
                "action_blocked",
                "action blocked by gate",
                [new ValidationProblem("$", gate.Message)]
            ).ToJson();
        }

        if (toolInterceptor is not null)
        {
            string? blockedMessage;
            try
            {
                blockedMessage = await toolInterceptor(message, name, effect, arguments, ct);
            }
            catch
            {
                await FinishAsync(ToolInvocationStatus.Faulted, null);
                throw;
            }
            if (blockedMessage is not null)
            {
                await FinishAsync(ToolInvocationStatus.Blocked, blockedMessage);
                return blockedMessage;
            }
        }

        object? result;
        try
        {
            ToolInputValidation.ValidateArguments(context.Function, context.Arguments);
            result = await next(context, ct);
        }
        catch (PaginationValidationException exception)
        {
            result = exception.Error;
        }
        catch (Exception exception) when (ToolInputValidation.IsExpected(exception, semantics))
        {
            result = ToolInputValidation.Error(exception.Message);
        }
        catch (Exception exception)
        {
            await FinishAsync(ToolInvocationStatus.Faulted, exception.Message);
            throw;
        }
        var toolError = result as ToolError;
        if (toolError is not null)
        {
            result = toolError.ToJson();
        }
        var isToolError =
            toolError is not null
            || IsMafToolFailure(result)
            || (effect == ToolEffect.ProcessExecution && IsFailedProcessExecution(result));
        ToolResultEvidence? resultEvidence;
        try
        {
            resultEvidence = semantics?.ResultEvidence?.Invoke(result);
        }
        catch
        {
            await FinishAsync(ToolInvocationStatus.Faulted, null);
            throw;
        }
        await RecordAsync(
            isToolError ? ToolInvocationStatus.Failed : ToolInvocationStatus.Completed,
            resultEvidence,
            ct
        );
        if (resultEvidence is ToolResultEvidence.Process diagnostic)
        {
            result = await DiagnosticResultAsync(message, actionInvocationId, diagnostic, ct);
        }
        await PublishUpdateAsync(
            message,
            new AgentUpdate.ToolCompleted(
                actionInvocationId,
                isToolError ? null : result?.ToString(),
                isToolError ? result?.ToString() ?? "Tool failed." : null
            ),
            ct
        );
        if (isToolError)
        {
            collector.RecordFailedToolCall(reservation, name);
            return result;
        }

        if (boundCapabilityNames.Contains(name))
        {
            collector.RecordLifecycleCall(name);
            capabilityInvocation.RecordResult(context.CallContent.CallId, result);
            context.Terminate = true;
        }
        else
        {
            collector.RecordSuccessfulToolCall(
                reservation,
                new ToolObservation(name, effect, semantics?.Evidence ?? ToolEvidence.None)
            );
        }

        return result;
    }

    private async ValueTask<JsonElement> DiagnosticResultAsync(
        PipelineMessage<TState> message,
        string actionInvocationId,
        ToolResultEvidence.Process diagnostic,
        CancellationToken cancellationToken
    )
    {
        var entryCursor = message.RunContext?.Ledger is { } outputLedger
            ? await outputLedger.FindActionEntryAsync(
                config.StepId,
                actionInvocationId,
                cancellationToken
            )
            : null;
        return JsonSerializer.SerializeToElement(
            new
            {
                exitCode = diagnostic.ExitCode,
                stdout = DiagnosticPreview(diagnostic.Stdout),
                stderr = DiagnosticPreview(diagnostic.Stderr),
                diagnostic.Duration,
                timedOut = diagnostic.TimedOut,
                captureTruncated = diagnostic.Truncated,
                previewTruncated = IsPreviewTruncated(diagnostic),
                stdoutCapturedCharacters = diagnostic.Stdout.Length,
                stderrCapturedCharacters = diagnostic.Stderr.Length,
                diagnostics = entryCursor is { } reference
                    ? new
                    {
                        entryCursor = reference,
                        tool = BuiltInAgentTools.ReadLedgerEntry,
                        streams = new[] { "stdout", "stderr" },
                    }
                    : null,
                retrieval = entryCursor is null
                    ? "No durable diagnostic reference is available; this is a bounded inline preview."
                    : "Use read_ledger_entry with entryCursor and stream stdout or stderr; follow nextOffset.",
            }
        );
    }

    // Tandem's own tools return ToolError. MAF's FileAccessProvider tools (write, replace,
    // delete) own their results and report failures only as text.
    private static bool IsMafToolFailure(object? result) =>
        result switch
        {
            string text when text.StartsWith("Error", StringComparison.OrdinalIgnoreCase) => true,
            _ => result?.ToString() is { } text
                && text.StartsWith("File '", StringComparison.Ordinal)
                && text.EndsWith("' not found.", StringComparison.Ordinal),
        };

    private static bool IsPreviewTruncated(ToolResultEvidence.Process process) =>
        process.Stdout.Length > DiagnosticPreviewCharacters
        || process.Stderr.Length > DiagnosticPreviewCharacters;

    private static string DiagnosticPreview(string text)
    {
        if (text.Length <= DiagnosticPreviewCharacters)
        {
            return text;
        }

        var start = text.Length - DiagnosticPreviewCharacters;
        if (char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
        {
            start++;
        }

        return text[start..];
    }

    private static bool IsFailedProcessExecution(object? result) =>
        result is JsonElement { ValueKind: JsonValueKind.Object } element
        && (
            element.TryGetProperty("exitCode", out var exitCode)
            || element.TryGetProperty("ExitCode", out exitCode)
        )
        && exitCode.TryGetInt32(out var value)
        && value != 0;
}
