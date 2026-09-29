using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Domain;

#pragma warning disable MAAI001

namespace Tandem.Infrastructure.Blocks;

internal sealed class AgentBlock<TState>(
    AgentBlockConfig<TState> config,
    IChatClient chatClient,
    Func<
        PipelineMessage<TState>,
        string,
        ToolEffect,
        JsonElement,
        CancellationToken,
        ValueTask<string?>
    >? toolInterceptor = null,
    Func<string, IChatClient>? chatClientFactory = null
)
    : Executor<PipelineMessage<TState>, PipelineMessage<TState>>(
        config.StepId,
        options: null,
        declareCrossRunShareable: true
    )
{
    private const int StructuredOutputCorrectionLimit = 1;
    private const int DiagnosticPreviewCharacters = 8000;
    private const string CheckpointGateId = "checkpoint-required";
    private static readonly TimeSpan _modelStreamIdleTimeout = TimeSpan.FromMinutes(20);

    public override async ValueTask<PipelineMessage<TState>> HandleAsync(
        PipelineMessage<TState> message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    ) => await ExecuteAsync(message, cancellationToken);

    public async ValueTask<PipelineMessage<TState>> ExecuteAsync(
        PipelineMessage<TState> message,
        CancellationToken cancellationToken
    )
    {
        var blockSw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (config.Timeout is { } timeout)
        {
            cts.CancelAfter(timeout);
        }

        var runtime = ApplyPreInvocationPolicies(message);
        message = message with { Runtime = runtime };
        var invocationId = runtime.NextInvocationId(config.StepId);
        var acceptedOutputId = $"{invocationId}--output";
        var requiresCheckpointRelease = IsCheckpointReleaseRequired(runtime);
        var capabilityInvocation = new CapabilityInvocationState<TState>(
            runtime.RunId,
            config.StepId,
            invocationId,
            message.State,
            message.RunContext
        );
        var capabilityFunctions = BindCapabilities(capabilityInvocation, message.RunContext);
        var collector = new ToolOutcomeCollector(runtime.Step(config.StepId).ToolInvocations);
        capabilityInvocation.AttachToolOutcomeCollector(collector);

        var instructions = requiresCheckpointRelease
            ? config.Checkpoint!.Instructions
            : InitialInstructions();
        var tools = capabilityFunctions.Cast<AITool>().ToList();
        var boundCapabilityNames = capabilityFunctions
            .Select(function => function.Name)
            .ToHashSet(StringComparer.Ordinal);
        var selectedChatClient = SelectChatClient(message);
        await PublishModelSelectedAsync(message, selectedChatClient, cts.Token);
        AIAgent CreateTurnAgent(string? requiredToolName) =>
            CreateAgent(
                instructions,
                tools,
                message,
                selectedChatClient,
                requiredToolName,
                collector,
                boundCapabilityNames,
                capabilityInvocation
            );

        var agent = CreateTurnAgent(
            requiresCheckpointRelease ? config.Checkpoint!.Capability.ToolName : null
        );
        var freshSession = runtime.Step(config.StepId).Session is null;
        var session = await RestoreOrCreateSessionAsync(agent, runtime, cts.Token);
        var userMessage = await BuildUserMessageAsync(
            message,
            requiresCheckpointRelease,
            cts.Token
        );
        IReadOnlyList<ChatMessage> turnInput =
            freshSession && !requiresCheckpointRelease
                ? ExampleConversation(message.State, userMessage)
                : [new ChatMessage(ChatRole.User, userMessage)];

        var priorUsage = runtime.Step(config.StepId).Usage;
        var cumulativeInputTokens = priorUsage?.CumulativeInputTokens ?? 0;
        var cumulativeOutputTokens = priorUsage?.CumulativeOutputTokens ?? 0;
        var continuationAttempt = 0;
        var policyExhausted = false;
        var structuredAttempt = 0;
        AgentStructuredOutputResult<TState>? structuredResult = null;

        while (true)
        {
            var turn = await StreamTurnAsync(
                agent,
                session,
                turnInput,
                message,
                cumulativeInputTokens,
                cumulativeOutputTokens,
                cts.Token
            );
            cumulativeInputTokens += turn.InputTokens ?? 0;
            cumulativeOutputTokens += turn.OutputTokens ?? 0;
            var turnUsage = ResolveUsage(
                turn.InputTokens,
                turn.OutputTokens,
                cumulativeInputTokens,
                cumulativeOutputTokens
            );
            var checkpointWasLatched = runtime.IsGateLatched(config.StepId, CheckpointGateId);
            runtime = LatchTriggeredGates(
                runtime.WithStep(config.StepId, step => step with { Usage = turnUsage }),
                turnUsage
            );
            message = message with { Runtime = runtime };
            capabilityInvocation.ThrowIfApplicationFaulted();
            if (capabilityInvocation.Accepted is not null)
            {
                break;
            }

            if (
                !checkpointWasLatched
                && runtime.IsGateLatched(config.StepId, CheckpointGateId)
                && config.Checkpoint is { } activatedCheckpoint
            )
            {
                requiresCheckpointRelease = true;
                instructions = activatedCheckpoint.Instructions;
                turnInput = UserTurn(
                    activatedCheckpoint.UserMessage(message.State, turnUsage.CurrentContextTokens)
                );
                agent = CreateTurnAgent(activatedCheckpoint.Capability.ToolName);
                continue;
            }

            if (config.StructuredOutput is { } structuredOutput)
            {
                structuredResult = await structuredOutput.EvaluateAsync(
                    turn.Text,
                    new StructuredOutputAttempt<TState>(
                        message,
                        config.StepId,
                        acceptedOutputId,
                        collector.SuccessfulTools,
                        collector.ToolInvocations,
                        structuredAttempt
                    ),
                    cts.Token
                );
                if (
                    structuredResult.Success
                    || structuredAttempt >= StructuredOutputCorrectionLimit
                )
                {
                    break;
                }

                await ObserveAsync(
                    message,
                    new PipelineStructuredOutputRejected(
                        runtime.RunId,
                        config.StepId,
                        structuredAttempt + 1,
                        structuredResult.Problems,
                        structuredResult.RawResponse
                    ),
                    cts.Token
                );
                structuredAttempt++;
                turnInput = UserTurn(
                    structuredResult.CorrectionPrompt(structuredOutput.JsonSchema)
                );
                continue;
            }

            if (
                requiresCheckpointRelease
                || collector.HasLifecycleCall
                || config.TurnPolicy is null
            )
            {
                break;
            }

            if (continuationAttempt >= config.TurnPolicy.MaxContinuationAttempts)
            {
                policyExhausted = true;
                break;
            }

            var directive = await config.TurnPolicy.Continue(
                message,
                turn.Text,
                turn.ToolNames,
                collector.HasLifecycleCall,
                continuationAttempt,
                cts.Token
            );
            if (directive is null)
            {
                policyExhausted = true;
                break;
            }

            continuationAttempt++;
            turnInput = UserTurn(directive.Prompt);
            agent = CreateTurnAgent(directive.RequiredToolName);
        }

        await InjectAcceptedCapabilityResultAsync(agent, session, capabilityInvocation, cts.Token);
        var updatedRuntime = CaptureToolInvocations(
            await CaptureSessionAsync(agent, session, runtime, cts.Token),
            collector
        );

        var outcome = ResolveOutcome(
            structuredResult,
            updatedRuntime,
            capabilityInvocation,
            requiresCheckpointRelease,
            policyExhausted,
            continuationAttempt,
            message.RunContext
        );
        blockSw.Stop();
        if (outcome.LatestOutcome is null)
        {
            return outcome;
        }
        var timedOutcome = outcome.LatestOutcome with { Duration = blockSw.Elapsed };
        return FinalizeConversation(outcome with { LatestOutcome = timedOutcome });
    }

    private AIFunction[] BindCapabilities(
        CapabilityInvocationState<TState> capabilityInvocation,
        PipelineRunContext? runContext
    )
    {
        var functions = config
            .Capabilities.Select(capability => capability.Bind(capabilityInvocation))
            .ToArray();
        if (
            runContext?.Ledger is not null
            && functions.Any(tool =>
                BuiltInAgentTools.Contains(BuiltInAgentTools.Ledger, tool.Name)
            )
        )
        {
            throw new InvalidOperationException(
                $"Agent '{config.StepId}' has a capability that collides with a ledger tool."
            );
        }
        if (
            (config.Skills?.Count ?? 0) > 0
            && functions.Any(tool =>
                BuiltInAgentTools.Contains(BuiltInAgentTools.Skills, tool.Name)
            )
        )
        {
            throw new InvalidOperationException(
                $"Agent '{config.StepId}' has a capability that collides with a skill tool."
            );
        }
        return functions;
    }

    private string InitialInstructions() =>
        string.Join(
            "\n\n",
            new[]
            {
                config.SystemInstructions,
                config.StructuredOutput is { } structuredOutput
                    ? AgentStructuredOutputPrompt.Initial(structuredOutput)
                    : null,
            }.Where(value => !string.IsNullOrWhiteSpace(value))
        );

    private async ValueTask<string> BuildUserMessageAsync(
        PipelineMessage<TState> message,
        bool requiresCheckpointRelease,
        CancellationToken cancellationToken
    )
    {
        if (requiresCheckpointRelease)
        {
            return config.Checkpoint!.UserMessage(
                message.State,
                message.Runtime.Step(config.StepId).Usage?.CurrentContextTokens ?? 0
            );
        }

        var augmentations = new List<string>();
        foreach (var augment in config.MessageAugmentations ?? [])
        {
            if (await augment(message, cancellationToken) is { } value)
            {
                augmentations.Add(value);
            }
        }
        var baseMessage = config.UserMessage!(message.State);
        return augmentations.Count > 0
            ? $"{baseMessage}\n\n{string.Join("\n\n", augmentations)}"
            : baseMessage;
    }

    private IReadOnlyList<ChatMessage> ExampleConversation(TState state, string userMessage) =>
        config.StructuredOutput?.Examples(state) is { Count: > 0 } examples
            ?
            [
                .. examples.SelectMany(example =>
                    new[]
                    {
                        new ChatMessage(ChatRole.User, example.Input),
                        new ChatMessage(ChatRole.Assistant, example.Output),
                    }
                ),
                new ChatMessage(ChatRole.User, userMessage),
            ]
            : UserTurn(userMessage);

    private static IReadOnlyList<ChatMessage> UserTurn(string text) =>
        [new ChatMessage(ChatRole.User, text)];

    private sealed record ModelTurn(
        string Text,
        IReadOnlyList<string> ToolNames,
        long? InputTokens,
        long? OutputTokens
    );

    private async ValueTask<ModelTurn> StreamTurnAsync(
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<ChatMessage> input,
        PipelineMessage<TState> message,
        long cumulativeInputTokens,
        long cumulativeOutputTokens,
        CancellationToken cancellationToken
    )
    {
        var text = new StringBuilder();
        var toolNames = new List<string>();
        long? inputTokens = null;
        long? outputTokens = null;
        var updates = agent.RunStreamingAsync(input, session, null, cancellationToken);
        await foreach (
            var update in WithIdleTimeout(updates, _modelStreamIdleTimeout, cancellationToken)
        )
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent textContent)
                {
                    text.Append(textContent.Text);
                }
                else if (content is FunctionCallContent functionCall)
                {
                    toolNames.Add(functionCall.Name);
                }
                else if (content is UsageContent usageContent)
                {
                    inputTokens = usageContent.Details.InputTokenCount;
                    outputTokens = usageContent.Details.OutputTokenCount;
                    var liveUsage = ResolveUsage(
                        inputTokens,
                        outputTokens,
                        cumulativeInputTokens,
                        cumulativeOutputTokens
                    );
                    await ObserveAsync(
                        message,
                        new PipelineAgentUsage(
                            message.Runtime.RunId,
                            config.StepId,
                            liveUsage.CurrentInputTokens,
                            liveUsage.CurrentOutputTokens,
                            liveUsage.CurrentContextTokens,
                            liveUsage.ContextWindowTokens,
                            ReasoningTokens: (int)(usageContent.Details.ReasoningTokenCount ?? 0)
                        ),
                        cancellationToken
                    );
                }
            }

            await PublishUpdatesAsync(message, update, cancellationToken);
        }
        return new ModelTurn(text.ToString(), toolNames, inputTokens, outputTokens);
    }

    // The accepted capability call terminates MAF's function loop. A plain ChatClientAgent
    // records that result in its session, but the Harness agent does not, so the result is
    // enqueued for the next request. It then arrives after the next user message; the
    // ToolResultAdjacencyChatClient moves it back next to its call (LocalCapabilityTests
    // *InTheMafSession prove both halves).
    private static async ValueTask InjectAcceptedCapabilityResultAsync(
        AIAgent agent,
        AgentSession session,
        CapabilityInvocationState<TState> capabilityInvocation,
        CancellationToken cancellationToken
    )
    {
        if (
            capabilityInvocation.AcceptedCallId is { } acceptedCallId
            && agent.GetService<MessageInjectingChatClient>() is { } injectingClient
        )
        {
            await injectingClient.EnqueueMessagesAsync(
                session,
                [
                    new ChatMessage(
                        ChatRole.Tool,
                        [
                            new FunctionResultContent(
                                acceptedCallId,
                                capabilityInvocation.AcceptedResult
                            ),
                        ]
                    ),
                ],
                cancellationToken
            );
        }
    }

    internal static async IAsyncEnumerable<T> WithIdleTimeout<T>(
        IAsyncEnumerable<T> source,
        TimeSpan idleTimeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = source.GetAsyncEnumerator(idleCts.Token);
        while (true)
        {
            idleCts.CancelAfter(idleTimeout);
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    yield break;
                }
            }
            catch (OperationCanceledException)
                when (idleCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Model stream produced no update for {idleTimeout.TotalSeconds:0} seconds."
                );
            }
            idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
            yield return enumerator.Current;
        }
    }

    private bool IsCheckpointReleaseRequired(PipelineRuntime runtime)
    {
        return config.Checkpoint is not null
            && runtime.IsGateLatched(config.StepId, "checkpoint-required");
    }

    private PipelineRuntime LatchTriggeredGates(PipelineRuntime runtime, AgentUsage usage)
    {
        foreach (var gate in config.LatchedGates ?? [])
        {
            if (!runtime.IsGateLatched(config.StepId, gate.Id) && gate.Trigger(usage))
            {
                runtime = runtime.WithStep(
                    config.StepId,
                    step => step with { Latches = step.Latches.Add(gate.Id) }
                );
            }
        }
        return runtime;
    }

    private AgentUsage ResolveUsage(
        long? inputTokens,
        long? outputTokens,
        long cumulativeInputTokens,
        long cumulativeOutputTokens
    )
    {
        var policy = config.Checkpoint;
        var contextWindow =
            config.ContextBudget?.ContextWindowTokens ?? policy?.ContextWindowTokens ?? 0;

        var input = (int)(inputTokens ?? 0);
        var output = (int)(outputTokens ?? 0);
        var currentContext = input + output;

        return new AgentUsage(
            CurrentInputTokens: input,
            CurrentOutputTokens: output,
            CurrentContextTokens: currentContext,
            ContextWindowTokens: contextWindow,
            CumulativeInputTokens: cumulativeInputTokens,
            CumulativeOutputTokens: cumulativeOutputTokens
        );
    }

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

    private PipelineMessage<TState> ResolveOutcome(
        AgentStructuredOutputResult<TState>? structuredResult,
        PipelineRuntime runtime,
        CapabilityInvocationState<TState> capabilityInvocation,
        bool requiresCheckpointRelease,
        bool policyExhausted,
        int continuationAttempt,
        PipelineRunContext? runContext
    )
    {
        PipelineMessage<TState> Outcome(
            TState state,
            string kind,
            string summary,
            JsonElement payload
        ) =>
            new(
                runtime.IncrementInvocations(config.StepId),
                state,
                new BlockOutcome(kind, config.StepId, summary, payload)
            )
            {
                RunContext = runContext,
            };

        var state = capabilityInvocation.State;
        if (capabilityInvocation.Accepted is { } accepted)
        {
            return ApplyAcceptedCapability(
                runtime,
                accepted,
                resetSession: requiresCheckpointRelease
                    && (config.Checkpoint?.ResetSessionAfterRelease ?? true),
                runContext
            );
        }

        if (requiresCheckpointRelease)
        {
            return Outcome(
                state,
                "agent.failed",
                $"Checkpoint-only mode: model did not call {config.Checkpoint!.Capability.ToolName}.",
                TandemJson.EmptyObject
            );
        }

        if (config.StructuredOutput is not null)
        {
            if (structuredResult is null)
            {
                throw new InvalidOperationException("Structured output was not evaluated.");
            }

            if (!structuredResult.Success)
            {
                return Outcome(
                    state,
                    "agent.failed",
                    "Structured output remained invalid after its corrective response.",
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            problems = structuredResult.Problems,
                            rawResponse = structuredResult.RawResponse,
                        }
                    )
                );
            }

            return Outcome(
                structuredResult.UpdatedState!,
                StandardOutcomeKinds.Success,
                "Succeeded",
                structuredResult.Payload
            );
        }

        return policyExhausted
            ? Outcome(
                state,
                "agent.failed",
                $"No lifecycle outcome after {continuationAttempt + 1} model turn(s).",
                JsonSerializer.SerializeToElement(
                    new { continuationAttempts = continuationAttempt }
                )
            )
            : Outcome(state, "agent.completed", "(no lifecycle call)", TandemJson.EmptyObject);
    }

    private PipelineMessage<TState> ApplyAcceptedCapability(
        PipelineRuntime runtime,
        AcceptedCapability<TState> accepted,
        bool resetSession,
        PipelineRunContext? runContext
    )
    {
        var updatedRuntime = runtime;
        foreach (
            var gate in (config.LatchedGates ?? []).Where(gate =>
                gate.ReleaseCapabilityId == accepted.CapabilityId
                && updatedRuntime.IsGateLatched(config.StepId, gate.Id)
            )
        )
        {
            updatedRuntime = updatedRuntime.WithStep(
                config.StepId,
                step => step with { Latches = step.Latches.Remove(gate.Id) }
            );
            if (gate.ResetSessionAfterRelease)
            {
                resetSession = true;
            }
        }
        if (resetSession)
        {
            updatedRuntime = updatedRuntime.WithStep(
                config.StepId,
                step => step with { Session = null, Usage = null }
            );
        }
        return new PipelineMessage<TState>(
            updatedRuntime.IncrementInvocations(config.StepId),
            accepted.State,
            new BlockOutcome(
                accepted.CapabilityId,
                config.StepId,
                accepted.Summary,
                accepted.Payload
            )
        )
        {
            RunContext = runContext,
        };
    }

    private AIAgent CreateAgent(
        string instructions,
        IReadOnlyList<AITool> tools,
        PipelineMessage<TState> message,
        IChatClient selectedChatClient,
        string? requiredToolName,
        ToolOutcomeCollector collector,
        IReadOnlySet<string> boundCapabilityNames,
        CapabilityInvocationState<TState> capabilityInvocation
    )
    {
        var chatOptions = CreateChatOptions(
            instructions,
            message.RunContext?.Ledger is { } ledger ? [.. tools, .. ledger.Tools] : tools
        );
        if (!string.IsNullOrWhiteSpace(requiredToolName))
        {
            chatOptions.ToolMode = ChatToolMode.RequireSpecific(requiredToolName);
        }

        var toolEffects = new ToolEffectRegistry();
        foreach (var capabilityName in boundCapabilityNames)
        {
            toolEffects.Add(capabilityName, ToolEffect.LifecycleTransition);
        }
        if (config.Skills is { Count: > 0 })
        {
            BuiltInAgentTools.Register(toolEffects, BuiltInAgentTools.Skills);
        }
        if (message.RunContext?.Ledger is not null)
        {
            BuiltInAgentTools.Register(toolEffects, BuiltInAgentTools.Ledger);
        }
        var hasGates =
            (config.StateGuards?.Count ?? 0) > 0 || (config.LatchedGates?.Count ?? 0) > 0;
        var workspace = ResolveWorkspace(message.State, boundCapabilityNames);
        var implementationContext = new AgentImplementationContext(
            config.StepId,
            selectedChatClient,
            chatOptions,
            workspace,
            toolEffects,
            config.Skills ?? [],
            config.ContextBudget?.ContextWindowTokens ?? config.Checkpoint?.ContextWindowTokens,
            config.ContextBudget?.MaxOutputTokens ?? config.Checkpoint?.MaxOutputTokens,
            config.ContextBudget?.DisableCompaction ?? config.Checkpoint?.DisableCompaction ?? false
        );
        var agent = config.ImplementationFactory is null
            ? new ChatClientAgent(
                selectedChatClient,
                new ChatClientAgentOptions
                {
                    Id = config.StepId,
                    Name = config.StepId,
                    ChatOptions = chatOptions,
                    AIContextProviders = config.Skills is { Count: > 0 } skills
                        ? [AgentSkillRuntime.CreateProvider(skills)]
                        : null,
                }
            )
            : config.ImplementationFactory(implementationContext);
        if (hasGates)
        {
            var unclassified = chatOptions.Tools?.FirstOrDefault(tool =>
                !toolEffects.TryGet(tool.Name, out _)
            );
            if (unclassified is not null)
            {
                throw new InvalidOperationException(
                    $"Gated agent '{config.StepId}' exposes unclassified action '{unclassified.Name}'."
                );
            }
        }

        return ConfigureFunctionInvocation(
            agent,
            collector,
            message,
            capabilityInvocation,
            boundCapabilityNames,
            toolEffects,
            workspace?.Path
        );
    }

    private ChatOptions CreateChatOptions(string instructions, IReadOnlyList<AITool> tools)
    {
        var options = new ChatOptions
        {
            Instructions = instructions,
            Tools = tools.ToList(),
            ResponseFormat = config.StructuredOutput?.ResponseFormat,
        };
        if (config.ModelRequestOptions is not { } request)
        {
            return options;
        }

        options.Reasoning = request.ReasoningEffort is { } effort
            ? new ReasoningOptions
            {
                Effort = effort switch
                {
                    AgentReasoningEffort.None => ReasoningEffort.None,
                    AgentReasoningEffort.Low => ReasoningEffort.Low,
                    AgentReasoningEffort.Medium => ReasoningEffort.Medium,
                    AgentReasoningEffort.High => ReasoningEffort.High,
                    _ => throw new InvalidOperationException("Unknown reasoning effort."),
                },
            }
            : null;
        if (request.ReasoningMaxTokens is { } reasoningMaxTokens)
        {
            options.AdditionalProperties = new() { ["reasoningMaxTokens"] = reasoningMaxTokens };
        }
        options.Temperature = request.Temperature;
        options.MaxOutputTokens = request.MaxOutputTokens;
        return options;
    }

    private ResolvedAgentWorkspace? ResolveWorkspace(
        TState state,
        IReadOnlySet<string> capabilityNames
    )
    {
        if (config.Workspace is not { } authored)
        {
            return null;
        }

        var path = authored.Path(state);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Agent '{config.StepId}' resolved a blank workspace path."
            );
        }
        var commands = authored.Commands(state);
        var active = authored.ToolGroups.Where(group => group.IsAvailable(state)).ToArray();
        var selections = active.SelectMany(group => group.Tools).ToArray();
        var selectedNames = selections
            .Where(selection => selection.Kind == AgentToolSelectionKind.BuiltIn)
            .Select(selection => selection.Name!)
            .ToHashSet(StringComparer.Ordinal);
        var includeCommands = selections.Any(selection =>
            selection.Kind == AgentToolSelectionKind.Commands
        );
        var selectedRegisteredNames = selections
            .Where(selection => selection.Kind == AgentToolSelectionKind.Registered)
            .Select(selection => selection.Name!)
            .ToHashSet(StringComparer.Ordinal);
        var selectedRegisteredTools = selectedRegisteredNames
            .Select(name =>
                (
                    authored.RegisteredTools
                    ?? new Dictionary<string, AgentWorkspaceToolDescriptor>()
                ).TryGetValue(name, out var tool)
                    ? tool
                    : throw new InvalidOperationException(
                        $"Unknown registered workspace tool '{name}'."
                    )
            )
            .ToArray();
        var selectedCommands = includeCommands ? commands : [];
        var reservedNames = new HashSet<string>(
            BuiltInAgentTools.ReservedWorkspaceNames,
            StringComparer.Ordinal
        );
        foreach (var command in selectedCommands)
        {
            if (!reservedNames.Add(command.Name) || capabilityNames.Contains(command.Name))
            {
                throw new InvalidOperationException(
                    $"Agent '{config.StepId}' exposes more than one tool named '{command.Name}'."
                );
            }
        }
        foreach (var tool in selectedRegisteredTools)
        {
            if (!reservedNames.Add(tool.Name) || capabilityNames.Contains(tool.Name))
            {
                throw new InvalidOperationException(
                    $"Agent '{config.StepId}' exposes more than one tool named '{tool.Name}'."
                );
            }
        }
        var capabilityCollision = capabilityNames.FirstOrDefault(reservedNames.Contains);
        if (capabilityCollision is not null)
        {
            throw new InvalidOperationException(
                $"Agent '{config.StepId}' has a capability that collides with workspace tool '{capabilityCollision}'."
            );
        }

        var fileTools = new HashSet<WorkspaceToolKind>();
        foreach (var name in selectedNames)
        {
            if (BuiltInAgentTools.FileSelections.TryGetValue(name, out var kind))
            {
                fileTools.Add(kind);
            }
            else if (!BuiltInAgentTools.Groups.ContainsKey(name))
            {
                throw new InvalidOperationException($"Unknown workspace tool '{name}'.");
            }
        }
        return new ResolvedAgentWorkspace(
            Path.GetFullPath(path),
            fileTools,
            selectedNames.Contains(BuiltInAgentTools.GitReadOnlyGroup),
            selectedNames.Contains(BuiltInAgentTools.ShellGroup),
            selectedNames.Contains(BuiltInAgentTools.WebSearchGroup),
            selectedNames.Contains(BuiltInAgentTools.WebFetchGroup),
            selectedCommands,
            selectedRegisteredTools
        );
    }

    private IReadOnlyList<ActiveAgentGate> ResolveActiveGates(PipelineMessage<TState> message)
    {
        var active = new List<ActiveAgentGate>();
        active.AddRange(
            (config.StateGuards ?? [])
                .Where(guard => guard.IsActive(message.State))
                .Select(guard => new ActiveAgentGate(
                    guard.Id,
                    guard.BlockedEffects,
                    guard.Message,
                    guard.RemediationCapabilityName
                ))
        );
        active.AddRange(
            (config.LatchedGates ?? [])
                .Where(gate => message.Runtime.IsGateLatched(config.StepId, gate.Id))
                .Select(gate => new ActiveAgentGate(
                    gate.Id,
                    gate.BlockedEffects,
                    gate.Message,
                    gate.ReleaseCapabilityName
                ))
        );
        return active;
    }

    private sealed record ActiveAgentGate(
        string Id,
        IReadOnlySet<ToolEffect> BlockedEffects,
        string Message,
        string? ReleaseCapabilityName
    );

    private async ValueTask PublishUpdatesAsync(
        PipelineMessage<TState> message,
        AgentResponseUpdate update,
        CancellationToken cancellationToken
    )
    {
        foreach (var content in update.Contents)
        {
            AgentUpdate? semantic = content switch
            {
                TextReasoningContent reasoning => new AgentUpdate.Reasoning(reasoning.Text),
                TextContent text => new AgentUpdate.Text(text.Text),
                UsageContent usage => new AgentUpdate.Usage(
                    usage.Details.InputTokenCount,
                    usage.Details.OutputTokenCount,
                    usage.Details.ReasoningTokenCount
                ),
                _ => null,
            };
            if (semantic is not null)
            {
                await PublishUpdateAsync(message, semantic, cancellationToken);
            }
        }
    }

    private IChatClient SelectChatClient(PipelineMessage<TState> message) =>
        new ToolResultAdjacencyChatClient(
            chatClientFactory is null
                ? chatClient
                : chatClientFactory(
                    message.Runtime.Step(config.StepId).Profile?.ProfileName ?? config.ProfileName
                )
        );

    private async ValueTask PublishModelSelectedAsync(
        PipelineMessage<TState> message,
        IChatClient selectedChatClient,
        CancellationToken cancellationToken
    )
    {
        if (
            selectedChatClient.GetService<ChatClientMetadata>()?.DefaultModelId is
            { Length: > 0 } modelId
        )
        {
            await PublishUpdateAsync(
                message,
                new AgentUpdate.ModelSelected(modelId),
                cancellationToken
            );
        }
    }

    private async ValueTask PublishUpdateAsync(
        PipelineMessage<TState> message,
        AgentUpdate update,
        CancellationToken cancellationToken
    ) =>
        await ObserveAsync(
            message,
            new PipelineAgentUpdated(message.Runtime.RunId, config.StepId, update),
            cancellationToken
        );

    private static async ValueTask ObserveAsync(
        PipelineMessage<TState> message,
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        if (message.RunContext is { } runContext)
        {
            await runContext.ObserveAsync(observation, cancellationToken);
        }
    }

    private PipelineMessage<TState> FinalizeConversation(PipelineMessage<TState> message)
    {
        if (config.RetainConversation is null || message.LatestOutcome is null)
        {
            return message;
        }
        if (config.RetainConversation(message, message.LatestOutcome))
        {
            return message;
        }

        return message with
        {
            Runtime = message.Runtime.WithStep(
                config.StepId,
                step => step.WithoutConversation() with { Profile = null }
            ),
        };
    }

    private PipelineRuntime ApplyPreInvocationPolicies(PipelineMessage<TState> message)
    {
        var runtime = message.Runtime;
        var profile =
            config.ProfilePolicy?.Invoke(message.State)
            ?? new AgentProfileSelection(config.ProfileName, "Configured agent profile.");
        return runtime.WithStep(
            config.StepId,
            step =>
                (
                    !config.ContinueSession
                    || (
                        step.Profile is { } current
                        && !string.Equals(
                            current.ProfileName,
                            profile.ProfileName,
                            StringComparison.Ordinal
                        )
                    )
                        ? step.WithoutConversation()
                        : step
                ) with
                {
                    Profile = profile,
                }
        );
    }

    private async Task<PipelineRuntime> CaptureSessionAsync(
        AIAgent agent,
        AgentSession session,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        return runtime.WithStep(config.StepId, step => step with { Session = serialized });
    }

    private PipelineRuntime CaptureToolInvocations(
        PipelineRuntime runtime,
        ToolOutcomeCollector collector
    ) =>
        runtime.WithStep(
            config.StepId,
            step => step with { ToolInvocations = collector.ToolInvocations }
        );

    private async Task<AgentSession> RestoreOrCreateSessionAsync(
        AIAgent agent,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        if (runtime.Step(config.StepId).Session is { } serialized)
        {
            return await agent.DeserializeSessionAsync(serialized, cancellationToken: ct);
        }

        return await agent.CreateSessionAsync(ct);
    }
}

#pragma warning restore MAAI001
