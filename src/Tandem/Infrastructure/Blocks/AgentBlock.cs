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
    Action<string, Guid, AgentUpdate>? onUpdate = null,
    Func<
        PipelineMessage<TState>,
        string,
        ToolEffect?,
        CancellationToken,
        ValueTask<string?>
    >? toolInterceptor = null,
    Action<ChatOptions>? configureChatOptions = null,
    Func<string, IChatClient>? chatClientFactory = null
)
    : Executor<PipelineMessage<TState>, PipelineMessage<TState>>(
        config.StepId,
        options: null,
        declareCrossRunShareable: true
    )
{
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
        var capabilityFunctions = config
            .Capabilities.Select(capability => capability.Bind(capabilityInvocation))
            .ToArray();
        if (
            (config.Skills?.Count ?? 0) > 0
            && capabilityFunctions.Any(tool =>
                tool.Name
                    is AgentSkillsProvider.LoadSkillToolName
                        or AgentSkillsProvider.ReadSkillResourceToolName
                        or AgentSkillsProvider.RunSkillScriptToolName
            )
        )
        {
            throw new InvalidOperationException(
                $"Agent '{config.StepId}' has a capability that collides with a skill tool."
            );
        }
        if (
            config.WorkspacePath is not null
            && capabilityFunctions.Any(tool =>
                tool.Name.StartsWith("file_access_", StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidOperationException(
                $"Agent '{config.StepId}' has a capability that collides with a Harness file tool."
            );
        }

        {
            var collector = new ToolOutcomeCollector();

            var instructions = requiresCheckpointRelease
                ? config.Checkpoint!.Instructions
                : string.Join(
                    "\n\n",
                    new[]
                    {
                        config.SystemInstructions,
                        config.StructuredOutput?.Instructions,
                    }.Where(value => !string.IsNullOrWhiteSpace(value))
                );
            var tools = capabilityFunctions.Cast<AITool>().ToList();
            var boundCapabilityNames = capabilityFunctions
                .Select(function => function.Name)
                .ToHashSet(StringComparer.Ordinal);
            var agent = CreateAgent(
                instructions,
                tools,
                message,
                requiredToolName: requiresCheckpointRelease
                    ? config.Checkpoint!.Capability.ToolName
                    : null,
                collector: collector,
                boundCapabilityNames: boundCapabilityNames
            );
            var freshSession = !runtime.AgentSessions.ContainsKey(config.StepId);
            var session = await RestoreOrCreateSessionAsync(agent, runtime, cts.Token);
            var baseMessage = requiresCheckpointRelease
                ? config.Checkpoint!.UserMessage(
                    message.State,
                    runtime.AgentUsage.GetValueOrDefault(config.StepId)?.CurrentContextTokens ?? 0
                )
                : config.ContextUserMessage?.Invoke(message) ?? config.UserMessage!(message.State);

            var augmentations = new List<string>();
            if (!requiresCheckpointRelease)
            {
                foreach (var augment in config.MessageAugmentations ?? [])
                {
                    if (await augment(message, cts.Token) is { } value)
                    {
                        augmentations.Add(value);
                    }
                }
            }
            var userMessage =
                augmentations.Count > 0
                    ? $"{baseMessage}\n\n{string.Join("\n\n", augmentations)}"
                    : baseMessage;
            IReadOnlyList<ChatMessage>? initialMessages = null;
            if (
                freshSession
                && !requiresCheckpointRelease
                && config.StructuredOutput?.Examples?.Invoke(message.State)
                    is { Count: > 0 } examples
            )
            {
                initialMessages =
                [
                    .. examples.SelectMany(example =>
                        new[]
                        {
                            new ChatMessage(ChatRole.User, example.Input),
                            new ChatMessage(ChatRole.Assistant, example.Output),
                        }
                    ),
                    new ChatMessage(ChatRole.User, userMessage),
                ];
            }

            long? inputTokens = null;
            long? outputTokens = null;
            var cumulativeInputTokens =
                runtime.AgentUsage.GetValueOrDefault(config.StepId)?.CumulativeInputTokens ?? 0;
            var cumulativeOutputTokens =
                runtime.AgentUsage.GetValueOrDefault(config.StepId)?.CumulativeOutputTokens ?? 0;
            var lastModelCallDuration = TimeSpan.Zero;
            var continuationAttempt = 0;
            var policyExhausted = false;
            var structuredAttempt = 0;
            AgentStructuredOutputResult<TState>? structuredResult = null;
            var structuredToolObservations = new HashSet<ToolObservationDescriptor>();

            while (true)
            {
                var turnText = new StringBuilder();
                var turnToolNames = new List<string>();
                var turnInputTokens = default(long?);
                var turnOutputTokens = default(long?);
                var turnSw = Stopwatch.StartNew();

                await foreach (
                    var update in initialMessages is null
                        ? agent.RunStreamingAsync(userMessage, session, null, cts.Token)
                        : agent.RunStreamingAsync(initialMessages, session, null, cts.Token)
                )
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is TextContent text)
                        {
                            turnText.Append(text.Text);
                        }
                        else if (content is FunctionCallContent functionCall)
                        {
                            turnToolNames.Add(functionCall.Name);
                        }
                        else if (content is UsageContent usageContent)
                        {
                            turnInputTokens = usageContent.Details.InputTokenCount;
                            turnOutputTokens = usageContent.Details.OutputTokenCount;
                        }
                    }

                    await PublishUpdatesAsync(message, runtime.RunId, update, cts.Token);
                }

                turnSw.Stop();
                initialMessages = null;
                inputTokens = turnInputTokens;
                outputTokens = turnOutputTokens;
                cumulativeInputTokens += turnInputTokens ?? 0;
                cumulativeOutputTokens += turnOutputTokens ?? 0;
                lastModelCallDuration += turnSw.Elapsed;
                var latestTurnUsage = ResolveUsage(
                    turnInputTokens,
                    turnOutputTokens,
                    cumulativeInputTokens,
                    cumulativeOutputTokens,
                    turnSw.Elapsed
                );
                var checkpointWasLatched = runtime.IsGateLatched(
                    config.StepId,
                    "checkpoint-required"
                );
                runtime = LatchTriggeredGates(
                    runtime.WithUsage(config.StepId, latestTurnUsage),
                    latestTurnUsage
                );
                message = message with { Runtime = runtime };
                capabilityInvocation.ThrowIfApplicationFaulted();
                foreach (var observation in collector.SuccessfulTools)
                {
                    structuredToolObservations.Add(observation);
                }

                if (capabilityInvocation.Accepted is not null)
                {
                    break;
                }

                if (
                    !checkpointWasLatched
                    && runtime.IsGateLatched(config.StepId, "checkpoint-required")
                    && config.Checkpoint is { } activatedCheckpoint
                )
                {
                    requiresCheckpointRelease = true;
                    instructions = activatedCheckpoint.Instructions;
                    userMessage = activatedCheckpoint.UserMessage(
                        message.State,
                        latestTurnUsage.CurrentContextTokens
                    );
                    agent = CreateAgent(
                        instructions,
                        tools,
                        message,
                        activatedCheckpoint.Capability.ToolName,
                        collector,
                        boundCapabilityNames
                    );
                    continue;
                }

                if (config.StructuredOutput is not null)
                {
                    structuredResult = config.StructuredOutput.Parse(
                        turnText.ToString(),
                        message.State
                    );
                    if (structuredResult.Success && config.StructuredOutput.Accept is not null)
                    {
                        var problems = config.StructuredOutput.Accept(
                            message,
                            structuredResult,
                            structuredToolObservations,
                            acceptedOutputId,
                            structuredAttempt
                        );
                        if (problems.Count > 0)
                        {
                            structuredResult = structuredResult with
                            {
                                Outcome = null,
                                Problems = [.. structuredResult.Problems, .. problems],
                            };
                        }
                    }
                    if (structuredResult.Success)
                    {
                        async ValueTask<bool> AcceptAsync(CancellationToken cancellationToken)
                        {
                            if (config.StructuredOutput.AcceptAsync is not null)
                            {
                                await config.StructuredOutput.AcceptAsync(
                                    message,
                                    structuredResult,
                                    structuredToolObservations,
                                    acceptedOutputId,
                                    structuredAttempt,
                                    cancellationToken
                                );
                            }
                            if (message.RunContext is { } observedRunContext)
                            {
                                await observedRunContext.ObserveAsync(
                                    new PipelineStructuredOutputAccepted(
                                        runtime.RunId,
                                        config.StepId,
                                        acceptedOutputId,
                                        structuredResult.Outcome!.Kind,
                                        config.StructuredOutput!.ValueType
                                            ?? config.StructuredOutput.OutputType?.FullName
                                            ?? config.StructuredOutput.OutputType?.Name,
                                        observedRunContext.ShouldPersist(config.StepId)
                                            ? structuredResult.Outcome!.Payload
                                            : null
                                    ),
                                    cancellationToken
                                );
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                            if (
                                config.StructuredOutput.Apply is { } apply
                                && structuredResult.Candidate is { } candidate
                            )
                            {
                                var mapped = apply(message.State, candidate);
                                structuredResult = structuredResult with
                                {
                                    Outcome = structuredResult.Outcome! with
                                    {
                                        UpdatedState = mapped,
                                    },
                                };
                            }
                            return true;
                        }

                        if (message.RunContext is { } structuredRunContext)
                        {
                            await structuredRunContext.ExecuteAsync(AcceptAsync, cts.Token);
                        }
                        else
                        {
                            await AcceptAsync(cts.Token);
                        }
                    }
                    if (structuredResult.Success || structuredAttempt >= 1)
                    {
                        break;
                    }

                    structuredAttempt++;
                    userMessage = structuredResult.CorrectionPrompt();
                    if (
                        !string.IsNullOrWhiteSpace(
                            config.StructuredOutput.CorrectionRequiredToolName
                        )
                    )
                    {
                        agent = CreateAgent(
                            instructions,
                            tools,
                            message,
                            config.StructuredOutput.CorrectionRequiredToolName,
                            collector,
                            boundCapabilityNames,
                            configureStructuredOutput: false
                        );
                    }
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
                    turnText.ToString(),
                    turnToolNames,
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
                userMessage = directive.Prompt;
                agent = CreateAgent(
                    instructions,
                    tools,
                    message,
                    directive.RequiredToolName,
                    collector,
                    boundCapabilityNames
                );
            }

            var agentUsage = ResolveUsage(
                inputTokens,
                outputTokens,
                cumulativeInputTokens,
                cumulativeOutputTokens,
                lastModelCallDuration
            );
            if (message.RunContext is { } usageRunContext)
            {
                await usageRunContext.ObserveAsync(
                    new PipelineAgentUsage(
                        runtime.RunId,
                        config.StepId,
                        agentUsage.CurrentInputTokens,
                        agentUsage.CurrentOutputTokens,
                        agentUsage.CurrentContextTokens
                    ),
                    cts.Token
                );
            }
            var runtimeAfterUsage = LatchTriggeredGates(
                runtime.WithUsage(config.StepId, agentUsage),
                agentUsage
            );

            var updatedRuntime = await CaptureSessionAsync(
                agent,
                session,
                runtimeAfterUsage,
                cts.Token
            );

            var outcome = await ResolveOutcomeAsync(
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
                runtime = runtime.WithGateLatch(config.StepId, gate.Id);
            }
        }
        return runtime;
    }

    private AgentUsage ResolveUsage(
        long? inputTokens,
        long? outputTokens,
        long cumulativeInputTokens,
        long cumulativeOutputTokens,
        TimeSpan elapsed
    )
    {
        var policy = config.Checkpoint;
        var contextWindow = policy?.ContextWindowTokens ?? 0;
        var checkpointAt = policy?.CheckpointAtTokens ?? 0;

        var input = (int)(inputTokens ?? 0);
        var output = (int)(outputTokens ?? 0);
        var currentContext = input + output;

        return new AgentUsage(
            CurrentInputTokens: input,
            CurrentOutputTokens: output,
            CurrentContextTokens: currentContext,
            ContextWindowTokens: contextWindow,
            CheckpointAtTokens: checkpointAt,
            LastModelCallDuration: elapsed,
            CumulativeInputTokens: cumulativeInputTokens,
            CumulativeOutputTokens: cumulativeOutputTokens
        );
    }

    private AIAgent ConfigureFunctionInvocation(
        AIAgent agent,
        ToolOutcomeCollector collector,
        PipelineMessage<TState> message,
        IReadOnlySet<string> boundCapabilityNames,
        ToolEffectRegistry toolEffects
    ) =>
        agent
            .AsBuilder()
            .Use(
                async (_, ficContext, next, ct) =>
                {
                    var isLifecycle = boundCapabilityNames.Contains(ficContext.Function.Name);
                    var classified = toolEffects.TryGet(
                        ficContext.Function.Name,
                        out var semantics
                    );
                    var effect = classified ? semantics.Effect.ToString() : "Unclassified";
                    if (message.RunContext is { } actionRunContext)
                    {
                        await actionRunContext.ObserveAsync(
                            new PipelineActionAttempted(
                                message.Runtime.RunId,
                                config.StepId,
                                message.Runtime.NextInvocationId(config.StepId),
                                ficContext.Function.Name,
                                effect
                            ),
                            ct
                        );
                    }

                    var activeGates = ResolveActiveGates(message);
                    var gate = activeGates.FirstOrDefault(active =>
                        (!classified || active.BlockedEffects.Contains(semantics.Effect))
                        && !string.Equals(
                            active.ReleaseCapabilityName,
                            ficContext.Function.Name,
                            StringComparison.Ordinal
                        )
                    );
                    if (gate is not null)
                    {
                        if (message.RunContext is { } gatedRunContext)
                        {
                            await gatedRunContext.ObserveAsync(
                                new PipelineActionCompleted(
                                    message.Runtime.RunId,
                                    config.StepId,
                                    message.Runtime.NextInvocationId(config.StepId),
                                    ficContext.Function.Name,
                                    effect,
                                    "Blocked"
                                ),
                                ct
                            );
                        }
                        return JsonSerializer.SerializeToElement(
                            new
                            {
                                isError = true,
                                error = "action blocked by gate",
                                problems = new[] { gate.Message },
                            }
                        );
                    }

                    if (toolInterceptor is not null)
                    {
                        var blockedMessage = await toolInterceptor(
                            message,
                            ficContext.Function.Name,
                            classified ? semantics.Effect : null,
                            ct
                        );
                        if (blockedMessage is not null)
                        {
                            if (message.RunContext is { } blockedRunContext)
                            {
                                await blockedRunContext.ObserveAsync(
                                    new PipelineActionCompleted(
                                        message.Runtime.RunId,
                                        config.StepId,
                                        message.Runtime.NextInvocationId(config.StepId),
                                        ficContext.Function.Name,
                                        effect,
                                        "Blocked"
                                    ),
                                    ct
                                );
                            }
                            return blockedMessage;
                        }
                    }

                    object? result;
                    try
                    {
                        result = await next(ficContext, ct);
                    }
                    catch
                    {
                        if (message.RunContext is { } failedRunContext)
                        {
                            await failedRunContext.ObserveAsync(
                                new PipelineActionCompleted(
                                    message.Runtime.RunId,
                                    config.StepId,
                                    message.Runtime.NextInvocationId(config.StepId),
                                    ficContext.Function.Name,
                                    effect,
                                    "Faulted"
                                ),
                                CancellationToken.None
                            );
                        }
                        throw;
                    }
                    if (message.RunContext is { } completedRunContext)
                    {
                        await completedRunContext.ObserveAsync(
                            new PipelineActionCompleted(
                                message.Runtime.RunId,
                                config.StepId,
                                message.Runtime.NextInvocationId(config.StepId),
                                ficContext.Function.Name,
                                effect,
                                IsToolError(result) ? "Failed" : "Completed"
                            ),
                            ct
                        );
                    }
                    if (IsToolError(result))
                    {
                        return result;
                    }

                    if (isLifecycle)
                    {
                        collector.RecordLifecycleCall(ficContext.Function.Name);
                        ficContext.Terminate = true;
                    }
                    else
                    {
                        collector.RecordSuccessfulToolCall(
                            new ToolObservationDescriptor(
                                ficContext.Function.Name,
                                classified ? semantics : null
                            )
                        );
                    }

                    return result;
                }
            )
            .Build();

    private static bool IsToolError(object? result) =>
        result switch
        {
            JsonElement element
                when element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("isError", out var isError)
                    && isError.ValueKind == JsonValueKind.True => true,
            string text when text.StartsWith("Error", StringComparison.OrdinalIgnoreCase) => true,
            string text
                when text.StartsWith("File '", StringComparison.Ordinal)
                    && text.EndsWith("' not found.", StringComparison.Ordinal) => true,
            _ => IsFileNotFoundResult(result),
        };

    private static bool IsFileNotFoundResult(object? result)
    {
        var text = result?.ToString();
        return text is not null
            && text.StartsWith("File '", StringComparison.Ordinal)
            && text.EndsWith("' not found.", StringComparison.Ordinal);
    }

    private Task<PipelineMessage<TState>> ResolveOutcomeAsync(
        AgentStructuredOutputResult<TState>? structuredResult,
        PipelineRuntime runtime,
        CapabilityInvocationState<TState> capabilityInvocation,
        bool requiresCheckpointRelease,
        bool policyExhausted,
        int continuationAttempt,
        PipelineRunContext? runContext
    )
    {
        var state = capabilityInvocation.State;
        if (requiresCheckpointRelease)
        {
            return Task.FromResult(
                capabilityInvocation.Accepted is { } checkpoint
                    ? ApplyAcceptedCapability(runtime, checkpoint, resetSession: true, runContext)
                    : new PipelineMessage<TState>(
                        runtime.IncrementInvocations(config.StepId),
                        state,
                        new BlockOutcome(
                            "agent.failed",
                            config.StepId,
                            $"Checkpoint-only mode: model did not call {config.Checkpoint!.Capability.ToolName}.",
                            EmptyPayload()
                        )
                    )
                    {
                        RunContext = runContext,
                    }
            );
        }

        if (capabilityInvocation.Accepted is null)
        {
            if (config.StructuredOutput is not null)
            {
                if (structuredResult is null)
                {
                    throw new InvalidOperationException("Structured output was not evaluated.");
                }

                if (!structuredResult.Success)
                {
                    return Task.FromResult(
                        new PipelineMessage<TState>(
                            runtime.IncrementInvocations(config.StepId),
                            state,
                            new BlockOutcome(
                                "agent.failed",
                                config.StepId,
                                "Structured output remained invalid after one correction.",
                                JsonSerializer.SerializeToElement(
                                    new
                                    {
                                        problems = structuredResult.Problems,
                                        rawResponse = structuredResult.RawResponse,
                                    }
                                )
                            )
                        )
                        {
                            RunContext = runContext,
                        }
                    );
                }

                var structured = structuredResult.Outcome!;
                var outcomeState = structured.UpdatedState is null
                    ? state
                    : structured.UpdatedState;
                return Task.FromResult(
                    new PipelineMessage<TState>(
                        runtime.IncrementInvocations(config.StepId),
                        outcomeState,
                        new BlockOutcome(
                            structured.Kind,
                            config.StepId,
                            structured.Summary,
                            structured.Payload
                        )
                    )
                    {
                        RunContext = runContext,
                    }
                );
            }

            var kind = policyExhausted ? "agent.failed" : "agent.completed";
            var summary = policyExhausted
                ? $"No lifecycle outcome after {continuationAttempt + 1} model turn(s)."
                : "(no lifecycle call)";
            var payload = policyExhausted
                ? JsonSerializer.SerializeToElement(
                    new { continuationAttempts = continuationAttempt }
                )
                : EmptyPayload();
            return Task.FromResult(
                new PipelineMessage<TState>(
                    runtime.IncrementInvocations(config.StepId),
                    state,
                    new BlockOutcome(kind, config.StepId, summary, payload)
                )
                {
                    RunContext = runContext,
                }
            );
        }

        return Task.FromResult(
            ApplyAcceptedCapability(
                runtime,
                capabilityInvocation.Accepted,
                resetSession: false,
                runContext
            )
        );
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
            updatedRuntime = updatedRuntime.WithoutGateLatch(config.StepId, gate.Id);
            if (gate.ResetSessionAfterRelease)
            {
                resetSession = true;
            }
        }
        if (resetSession)
        {
            updatedRuntime = updatedRuntime
                .WithoutSession(config.StepId)
                .WithoutUsage(config.StepId);
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
        string? requiredToolName,
        ToolOutcomeCollector collector,
        IReadOnlySet<string> boundCapabilityNames,
        bool configureStructuredOutput = true
    )
    {
        var chatOptions = new ChatOptions
        {
            Instructions = $"{GenericAgentInstructions.Value}\n\n{instructions}",
            Tools = tools.ToList(),
        };
        if (!string.IsNullOrWhiteSpace(requiredToolName))
        {
            chatOptions.ToolMode = ChatToolMode.RequireSpecific(requiredToolName);
        }
        if (configureStructuredOutput)
        {
            configureChatOptions?.Invoke(chatOptions);
        }

        var selectedChatClient = chatClientFactory is null
            ? chatClient
            : chatClientFactory(
                message.Runtime.AgentProfiles.GetValueOrDefault(config.StepId)?.ProfileName
                    ?? config.ProfileName
            );
        var toolEffects = new ToolEffectRegistry();
        foreach (var capabilityName in boundCapabilityNames)
        {
            toolEffects.Add(capabilityName, ToolEffect.LifecycleTransition);
        }
        var allowMutation = config.AllowMutation?.Invoke(message.State) ?? false;
        var hasGates =
            (config.StateGuards?.Count ?? 0) > 0 || (config.LatchedGates?.Count ?? 0) > 0;
        var implementationContext = new AgentImplementationContext(
            config.StepId,
            selectedChatClient,
            chatOptions,
            config.WorkspacePath?.Invoke(message.State),
            allowMutation || toolInterceptor is not null || hasGates,
            toolEffects,
            config.Skills ?? []
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
            boundCapabilityNames,
            toolEffects
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

    private static JsonElement EmptyPayload() => JsonSerializer.SerializeToElement(new { });

    private sealed record ActiveAgentGate(
        string Id,
        IReadOnlySet<ToolEffect> BlockedEffects,
        string Message,
        string? ReleaseCapabilityName
    );

    private async ValueTask PublishUpdatesAsync(
        PipelineMessage<TState> message,
        Guid runId,
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
                FunctionCallContent call => new AgentUpdate.ToolStarted(
                    call.CallId,
                    call.Name,
                    JsonSerializer.SerializeToElement(call.Arguments)
                ),
                FunctionResultContent result => new AgentUpdate.ToolCompleted(
                    result.CallId,
                    result.Result?.ToString(),
                    result.Exception?.Message
                ),
                _ => null,
            };

            if (semantic is not null)
            {
                onUpdate?.Invoke(config.StepId, runId, semantic);
                if (message.RunContext is not null)
                {
                    await message.RunContext.ObserveAsync(
                        new PipelineAgentUpdated(runId, config.StepId, semantic),
                        cancellationToken
                    );
                }
            }
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
            Runtime = message
                .Runtime.WithoutSession(config.StepId)
                .WithoutUsage(config.StepId)
                .WithoutProfile(config.StepId),
        };
    }

    private PipelineRuntime ApplyPreInvocationPolicies(PipelineMessage<TState> message)
    {
        var runtime = message.Runtime;
        if (!config.ContinueSession)
        {
            runtime = runtime.WithoutSession(config.StepId).WithoutUsage(config.StepId);
        }

        var profile =
            config.ProfilePolicy?.Invoke(message.State)
            ?? new AgentProfileSelection(config.ProfileName, "Configured agent profile.");
        if (
            runtime.AgentProfiles.TryGetValue(config.StepId, out var currentProfile)
            && !string.Equals(
                currentProfile.ProfileName,
                profile.ProfileName,
                StringComparison.Ordinal
            )
        )
        {
            runtime = runtime.WithoutSession(config.StepId).WithoutUsage(config.StepId);
        }
        return runtime.WithProfile(config.StepId, profile);
    }

    private async Task<PipelineRuntime> CaptureSessionAsync(
        AIAgent agent,
        AgentSession session,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        return runtime.WithSession(config.StepId, serialized);
    }

    private async Task<AgentSession> RestoreOrCreateSessionAsync(
        AIAgent agent,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        if (runtime.AgentSessions.TryGetValue(config.StepId, out var serialized))
        {
            return await agent.DeserializeSessionAsync(serialized, cancellationToken: ct);
        }

        return await agent.CreateSessionAsync(ct);
    }
}

internal sealed class ToolOutcomeCollector
{
    private string? _lifecycleToolName;
    private readonly HashSet<ToolObservationDescriptor> _successfulTools = [];

    public bool HasLifecycleCall => _lifecycleToolName is not null;

    public void RecordLifecycleCall(string toolName) => _lifecycleToolName ??= toolName;

    public void RecordSuccessfulToolCall(ToolObservationDescriptor observation) =>
        _successfulTools.Add(observation);

    public IReadOnlySet<ToolObservationDescriptor> SuccessfulTools => _successfulTools;

    public string? LifecycleToolName => _lifecycleToolName;
}

#pragma warning restore MAAI001
