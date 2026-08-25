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
        JsonElement,
        CancellationToken,
        ValueTask<string?>
    >? toolInterceptor = null,
    Action<ChatOptions>? configureChatOptions = null,
    Func<string, IChatClient>? chatClientFactory = null,
    Action<ChatOptions>? configureModelRequestOptions = null
)
    : Executor<PipelineMessage<TState>, PipelineMessage<TState>>(
        config.StepId,
        options: null,
        declareCrossRunShareable: true
    )
{
    private const int StructuredOutputCorrectionLimit = 2;
    private static readonly TimeSpan _modelStreamIdleTimeout = TimeSpan.FromMinutes(20);
    private static readonly HashSet<string> _reservedWorkspaceToolNames =
    [
        "read_file",
        "ls",
        "grep",
        "write_file",
        "delete_file",
        "replace",
        "replace_lines",
        "git:ro",
        "shell",
        "web_search",
        "web_fetch",
        "git_status",
        "git_diff",
        "git_log",
        "git_show",
        "git_blame",
        "git_changed_files",
        "git_compare",
        "run_shell",
        AgentSkillsProvider.LoadSkillToolName,
        AgentSkillsProvider.ReadSkillResourceToolName,
        AgentSkillsProvider.RunSkillScriptToolName,
    ];

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
            message.RunContext?.Ledger is not null
            && capabilityFunctions.Any(tool => tool.Name is "read_ledger" or "search_ledger")
        )
        {
            throw new InvalidOperationException(
                $"Agent '{config.StepId}' has a capability that collides with a ledger tool."
            );
        }
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
        {
            var collector = new ToolOutcomeCollector(RestoreToolInvocations(runtime));
            capabilityInvocation.AttachToolOutcomeCollector(collector);

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
            var selectedChatClient = SelectChatClient(message);
            await PublishModelSelectedAsync(message, selectedChatClient, cts.Token);
            var agent = CreateAgent(
                instructions,
                tools,
                message,
                selectedChatClient,
                requiredToolName: requiresCheckpointRelease
                    ? config.Checkpoint!.Capability.ToolName
                    : null,
                collector: collector,
                boundCapabilityNames: boundCapabilityNames,
                capabilityInvocation: capabilityInvocation
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

                var updates = initialMessages is null
                    ? agent.RunStreamingAsync(userMessage, session, null, cts.Token)
                    : agent.RunStreamingAsync(initialMessages, session, null, cts.Token);
                await foreach (
                    var update in WithIdleTimeout(updates, _modelStreamIdleTimeout, cts.Token)
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
                            if (message.RunContext is { } usageRunContext)
                            {
                                var liveUsage = ResolveUsage(
                                    turnInputTokens,
                                    turnOutputTokens,
                                    cumulativeInputTokens,
                                    cumulativeOutputTokens,
                                    turnSw.Elapsed
                                );
                                await usageRunContext.ObserveAsync(
                                    new PipelineAgentUsage(
                                        runtime.RunId,
                                        config.StepId,
                                        liveUsage.CurrentInputTokens,
                                        liveUsage.CurrentOutputTokens,
                                        liveUsage.CurrentContextTokens,
                                        liveUsage.ContextWindowTokens
                                    ),
                                    cts.Token
                                );
                            }
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
                structuredToolObservations.Clear();
                structuredToolObservations.UnionWith(collector.SuccessfulTools);
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
                        selectedChatClient,
                        activatedCheckpoint.Capability.ToolName,
                        collector,
                        boundCapabilityNames,
                        capabilityInvocation
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
                            collector.ToolInvocations,
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
                                    collector.ToolInvocations,
                                    acceptedOutputId,
                                    structuredAttempt,
                                    cancellationToken
                                );
                            }
                            if (message.RunContext is { } observedRunContext)
                            {
                                await observedRunContext.ObserveAsync(
                                    (
                                        config.StructuredOutput!.EmitAccepted is { } emit
                                            ? emit(
                                                runtime.RunId,
                                                config.StepId,
                                                acceptedOutputId,
                                                structuredResult.Outcome!.Kind,
                                                observedRunContext.ShouldPersist(config.StepId)
                                                    ? structuredResult.Outcome!.Payload
                                                    : null,
                                                structuredResult.Candidate!
                                            )
                                            : new PipelineStructuredOutputAccepted(
                                                runtime.RunId,
                                                config.StepId,
                                                acceptedOutputId,
                                                structuredResult.Outcome!.Kind,
                                                config.StructuredOutput.ValueType
                                                    ?? config.StructuredOutput.OutputType?.FullName
                                                    ?? config.StructuredOutput.OutputType?.Name,
                                                observedRunContext.ShouldPersist(config.StepId)
                                                    ? structuredResult.Outcome!.Payload
                                                    : null
                                            )
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
                    if (
                        structuredResult.Success
                        || structuredAttempt >= StructuredOutputCorrectionLimit
                    )
                    {
                        break;
                    }

                    if (message.RunContext is { } rejectionRunContext)
                    {
                        await rejectionRunContext.ObserveAsync(
                            new PipelineStructuredOutputRejected(
                                runtime.RunId,
                                config.StepId,
                                structuredAttempt + 1,
                                structuredResult
                                    .Problems.Select(problem => new PipelineStructuredOutputProblem(
                                        problem.Field,
                                        problem.Message
                                    ))
                                    .ToArray(),
                                structuredResult.RawResponse
                            ),
                            cts.Token
                        );
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
                            selectedChatClient,
                            config.StructuredOutput.CorrectionRequiredToolName,
                            collector,
                            boundCapabilityNames,
                            capabilityInvocation,
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
                    selectedChatClient,
                    directive.RequiredToolName,
                    collector,
                    boundCapabilityNames,
                    capabilityInvocation
                );
            }

            var agentUsage = ResolveUsage(
                inputTokens,
                outputTokens,
                cumulativeInputTokens,
                cumulativeOutputTokens,
                lastModelCallDuration
            );
            var runtimeAfterUsage = LatchTriggeredGates(
                runtime.WithUsage(config.StepId, agentUsage),
                agentUsage
            );

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
                    cts.Token
                );
            }
            var updatedRuntime = CaptureToolInvocations(
                await CaptureSessionAsync(agent, session, runtimeAfterUsage, cts.Token),
                collector
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
        CapabilityInvocationState<TState> capabilityInvocation,
        IReadOnlySet<string> boundCapabilityNames,
        ToolEffectRegistry toolEffects,
        string? workingDirectory
    ) =>
        agent
            .AsBuilder()
            .Use(
                async (_, ficContext, next, ct) =>
                {
                    var reservation = collector.ReserveToolInvocation();
                    var isLifecycle = boundCapabilityNames.Contains(ficContext.Function.Name);
                    var classified = toolEffects.TryGet(
                        ficContext.Function.Name,
                        out var semantics
                    );
                    var effect = classified ? semantics.Effect.ToString() : "Unclassified";
                    var actionInvocationId =
                        $"{message.Runtime.NextInvocationId(config.StepId)}--action-{reservation.Ordinal + 1}";
                    if (message.RunContext is { } actionRunContext)
                    {
                        await actionRunContext.ObserveAsync(
                            new PipelineActionAttempted(
                                message.Runtime.RunId,
                                config.StepId,
                                actionInvocationId,
                                ficContext.Function.Name,
                                effect
                            ),
                            ct
                        );
                    }

                    JsonElement arguments;
                    try
                    {
                        arguments = JsonSerializer.SerializeToElement(
                            ficContext.Arguments,
                            TandemJson.TypedContract
                        );
                    }
                    catch
                    {
                        collector.CompleteToolInvocation(
                            reservation,
                            new ToolInvocationObservationDescriptor(
                                ficContext.Function.Name,
                                classified ? semantics : null,
                                JsonSerializer.SerializeToElement(new { }),
                                ToolInvocationStatus.Faulted,
                                null
                            )
                        );
                        collector.RecordFailedToolCall(reservation, ficContext.Function.Name);
                        if (message.RunContext is { } serializationFailedRunContext)
                        {
                            await serializationFailedRunContext.ObserveAsync(
                                new PipelineActionCompleted(
                                    message.Runtime.RunId,
                                    config.StepId,
                                    actionInvocationId,
                                    ficContext.Function.Name,
                                    effect,
                                    "Faulted"
                                ),
                                CancellationToken.None
                            );
                        }
                        throw;
                    }
                    await PublishUpdateAsync(
                        message,
                        new AgentUpdate.ToolStarted(
                            actionInvocationId,
                            ficContext.Function.Name,
                            arguments
                        )
                        {
                            WorkingDirectory = workingDirectory,
                        },
                        ct
                    );

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
                        collector.CompleteToolInvocation(
                            reservation,
                            new ToolInvocationObservationDescriptor(
                                ficContext.Function.Name,
                                classified ? semantics : null,
                                arguments,
                                ToolInvocationStatus.Blocked,
                                null
                            )
                        );
                        collector.RecordFailedToolCall(reservation, ficContext.Function.Name);
                        if (message.RunContext is { } gatedRunContext)
                        {
                            await gatedRunContext.ObserveAsync(
                                new PipelineActionCompleted(
                                    message.Runtime.RunId,
                                    config.StepId,
                                    actionInvocationId,
                                    ficContext.Function.Name,
                                    effect,
                                    "Blocked"
                                ),
                                ct
                            );
                        }
                        await PublishUpdateAsync(
                            message,
                            new AgentUpdate.ToolCompleted(
                                actionInvocationId,
                                null,
                                "Action blocked by gate."
                            ),
                            ct
                        );
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
                        string? blockedMessage;
                        try
                        {
                            blockedMessage = await toolInterceptor(
                                message,
                                ficContext.Function.Name,
                                classified ? semantics.Effect : null,
                                arguments,
                                ct
                            );
                        }
                        catch
                        {
                            collector.CompleteToolInvocation(
                                reservation,
                                new ToolInvocationObservationDescriptor(
                                    ficContext.Function.Name,
                                    classified ? semantics : null,
                                    arguments,
                                    ToolInvocationStatus.Faulted,
                                    null
                                )
                            );
                            collector.RecordFailedToolCall(reservation, ficContext.Function.Name);
                            if (message.RunContext is { } interceptorFailedRunContext)
                            {
                                await interceptorFailedRunContext.ObserveAsync(
                                    new PipelineActionCompleted(
                                        message.Runtime.RunId,
                                        config.StepId,
                                        actionInvocationId,
                                        ficContext.Function.Name,
                                        effect,
                                        "Faulted"
                                    ),
                                    CancellationToken.None
                                );
                            }
                            throw;
                        }
                        if (blockedMessage is not null)
                        {
                            collector.CompleteToolInvocation(
                                reservation,
                                new ToolInvocationObservationDescriptor(
                                    ficContext.Function.Name,
                                    classified ? semantics : null,
                                    arguments,
                                    ToolInvocationStatus.Blocked,
                                    null
                                )
                            );
                            collector.RecordFailedToolCall(reservation, ficContext.Function.Name);
                            if (message.RunContext is { } blockedRunContext)
                            {
                                await blockedRunContext.ObserveAsync(
                                    new PipelineActionCompleted(
                                        message.Runtime.RunId,
                                        config.StepId,
                                        actionInvocationId,
                                        ficContext.Function.Name,
                                        effect,
                                        "Blocked"
                                    ),
                                    ct
                                );
                            }
                            await PublishUpdateAsync(
                                message,
                                new AgentUpdate.ToolCompleted(
                                    actionInvocationId,
                                    null,
                                    blockedMessage
                                ),
                                ct
                            );
                            return blockedMessage;
                        }
                    }

                    object? result;
                    try
                    {
                        result = await next(ficContext, ct);
                    }
                    catch (Exception exception)
                    {
                        collector.CompleteToolInvocation(
                            reservation,
                            new ToolInvocationObservationDescriptor(
                                ficContext.Function.Name,
                                classified ? semantics : null,
                                arguments,
                                ToolInvocationStatus.Faulted,
                                null
                            )
                        );
                        collector.RecordFailedToolCall(reservation, ficContext.Function.Name);
                        if (message.RunContext is { } failedRunContext)
                        {
                            await failedRunContext.ObserveAsync(
                                new PipelineActionCompleted(
                                    message.Runtime.RunId,
                                    config.StepId,
                                    actionInvocationId,
                                    ficContext.Function.Name,
                                    effect,
                                    "Faulted"
                                ),
                                CancellationToken.None
                            );
                        }
                        await PublishUpdateAsync(
                            message,
                            new AgentUpdate.ToolCompleted(
                                actionInvocationId,
                                null,
                                exception.Message
                            ),
                            CancellationToken.None
                        );
                        throw;
                    }
                    var isToolError =
                        IsToolError(result)
                        || (
                            classified
                            && semantics.Effect == ToolEffect.ProcessExecution
                            && IsFailedProcessExecution(result)
                        );
                    ToolResultEvidenceDescriptor? resultEvidence;
                    try
                    {
                        resultEvidence = classified
                            ? semantics.ResultEvidence?.Invoke(result)
                            : null;
                    }
                    catch
                    {
                        collector.CompleteToolInvocation(
                            reservation,
                            new ToolInvocationObservationDescriptor(
                                ficContext.Function.Name,
                                classified ? semantics : null,
                                arguments,
                                ToolInvocationStatus.Faulted,
                                null
                            )
                        );
                        collector.RecordFailedToolCall(reservation, ficContext.Function.Name);
                        if (message.RunContext is { } evidenceFailedRunContext)
                        {
                            await evidenceFailedRunContext.ObserveAsync(
                                new PipelineActionCompleted(
                                    message.Runtime.RunId,
                                    config.StepId,
                                    actionInvocationId,
                                    ficContext.Function.Name,
                                    effect,
                                    "Faulted"
                                ),
                                CancellationToken.None
                            );
                        }
                        throw;
                    }
                    collector.CompleteToolInvocation(
                        reservation,
                        new ToolInvocationObservationDescriptor(
                            ficContext.Function.Name,
                            classified ? semantics : null,
                            arguments,
                            isToolError
                                ? ToolInvocationStatus.Failed
                                : ToolInvocationStatus.Completed,
                            resultEvidence
                        )
                    );
                    if (message.RunContext is { } completedRunContext)
                    {
                        await completedRunContext.ObserveAsync(
                            new PipelineActionCompleted(
                                message.Runtime.RunId,
                                config.StepId,
                                actionInvocationId,
                                ficContext.Function.Name,
                                effect,
                                isToolError ? "Failed" : "Completed",
                                resultEvidence is ToolResultEvidenceDescriptor.Process process
                                    ? new PipelineActionProcessPayload(
                                        arguments,
                                        process.ExitCode,
                                        process.Stdout,
                                        process.Stderr,
                                        process.Duration,
                                        process.TimedOut,
                                        process.Truncated
                                    )
                                    : null
                            ),
                            ct
                        );
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
                        collector.RecordFailedToolCall(reservation, ficContext.Function.Name);
                        return result;
                    }

                    if (isLifecycle)
                    {
                        collector.RecordLifecycleCall(ficContext.Function.Name);
                        capabilityInvocation.RecordResult(ficContext.CallContent.CallId, result);
                        ficContext.Terminate = true;
                    }
                    else
                    {
                        collector.RecordSuccessfulToolCall(
                            reservation,
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

    private static bool IsFailedProcessExecution(object? result) =>
        result is JsonElement { ValueKind: JsonValueKind.Object } element
        && (
            element.TryGetProperty("exitCode", out var exitCode)
            || element.TryGetProperty("ExitCode", out exitCode)
        )
        && exitCode.TryGetInt32(out var value)
        && value != 0;

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
                    ? ApplyAcceptedCapability(
                        runtime,
                        checkpoint,
                        resetSession: config.Checkpoint?.ResetSessionAfterRelease ?? true,
                        runContext
                    )
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
                                $"Structured output remained invalid after {StructuredOutputCorrectionLimit} corrections.",
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
        IChatClient selectedChatClient,
        string? requiredToolName,
        ToolOutcomeCollector collector,
        IReadOnlySet<string> boundCapabilityNames,
        CapabilityInvocationState<TState> capabilityInvocation,
        bool configureStructuredOutput = true
    )
    {
        var chatOptions = new ChatOptions
        {
            Instructions = $"{GenericAgentInstructions.Value}\n\n{instructions}",
            Tools = tools.ToList(),
        };
        configureModelRequestOptions?.Invoke(chatOptions);
        if (message.RunContext?.Ledger is { } ledger)
        {
            chatOptions.Tools.Add(
                AIFunctionFactory.Create(
                    (
                        [System.ComponentModel.Description("Cursor returned by the previous page.")]
                            long? cursor = null,
                        [System.ComponentModel.Description("Page size from 1 to 50.")]
                            int limit = 20,
                        CancellationToken cancellationToken = default
                    ) => ledger.ReadAsync(cursor, limit, cancellationToken),
                    "read_ledger",
                    "Read accepted durable lifecycle history in order: claims, decisions, findings, checkpoints, state, and transitions. Repository and implementation claims in those records must be verified against the current repository before reliance."
                )
            );
            chatOptions.Tools.Add(
                AIFunctionFactory.Create(
                    (
                        [System.ComponentModel.Description(
                            "Case-insensitive text to find in accepted durable records."
                        )]
                            string query,
                        [System.ComponentModel.Description("Cursor returned by the previous page.")]
                            long? cursor = null,
                        [System.ComponentModel.Description("Page size from 1 to 50.")]
                            int limit = 20,
                        CancellationToken cancellationToken = default
                    ) => ledger.SearchAsync(query, cursor, limit, cancellationToken),
                    "search_ledger",
                    "Search accepted durable lifecycle history for relevant prior claims, decisions, findings, constraints, checkpoints, and state, then use read_ledger to inspect surrounding records. A match does not establish current repository or implementation state."
                )
            );
        }
        if (!string.IsNullOrWhiteSpace(requiredToolName))
        {
            chatOptions.ToolMode = ChatToolMode.RequireSpecific(requiredToolName);
        }
        if (configureStructuredOutput)
        {
            configureChatOptions?.Invoke(chatOptions);
        }

        var toolEffects = new ToolEffectRegistry();
        foreach (var capabilityName in boundCapabilityNames)
        {
            toolEffects.Add(capabilityName, ToolEffect.LifecycleTransition);
        }
        if (config.Skills is { Count: > 0 })
        {
            AgentSkillRuntime.RegisterToolEffects(toolEffects);
        }
        if (message.RunContext?.Ledger is not null)
        {
            toolEffects.Add("read_ledger", ToolEffect.Read);
            toolEffects.Add("search_ledger", ToolEffect.Read);
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
            config.Checkpoint?.ContextWindowTokens,
            config.Checkpoint?.MaxOutputTokens,
            config.Checkpoint?.DisableCompaction ?? false
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
            _reservedWorkspaceToolNames,
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
            var kind = name switch
            {
                "read_file" => WorkspaceToolKind.ReadFile,
                "ls" => WorkspaceToolKind.ListFiles,
                "grep" => WorkspaceToolKind.Grep,
                "write_file" => WorkspaceToolKind.WriteFile,
                "delete_file" => WorkspaceToolKind.DeleteFile,
                "replace" => WorkspaceToolKind.Replace,
                "replace_lines" => WorkspaceToolKind.ReplaceLines,
                "copy_file" => WorkspaceToolKind.CopyFile,
                "move_file" => WorkspaceToolKind.MoveFile,
                "create_directory" => WorkspaceToolKind.CreateDirectory,
                "git:ro" or "shell" or "web_search" or "web_fetch" => default,
                _ => throw new InvalidOperationException($"Unknown workspace tool '{name}'."),
            };
            if (name is not "git:ro" and not "shell" and not "web_search" and not "web_fetch")
            {
                fileTools.Add(kind);
            }
        }
        return new ResolvedAgentWorkspace(
            Path.GetFullPath(path),
            fileTools,
            selectedNames.Contains("git:ro"),
            selectedNames.Contains("shell"),
            selectedNames.Contains("web_search"),
            selectedNames.Contains("web_fetch"),
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
                _ => null,
            };

            if (content is FunctionResultContent result)
            {
                onUpdate?.Invoke(
                    config.StepId,
                    runId,
                    new AgentUpdate.ToolCompleted(
                        result.CallId,
                        result.Result?.ToString(),
                        result.Exception?.Message
                    )
                );
                continue;
            }

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

    private IChatClient SelectChatClient(PipelineMessage<TState> message) =>
        new ToolResultAdjacencyChatClient(
            chatClientFactory is null
                ? chatClient
                : chatClientFactory(
                    message.Runtime.AgentProfiles.GetValueOrDefault(config.StepId)?.ProfileName
                        ?? config.ProfileName
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
    )
    {
        onUpdate?.Invoke(config.StepId, message.Runtime.RunId, update);
        if (message.RunContext is not null)
        {
            await message.RunContext.ObserveAsync(
                new PipelineAgentUpdated(message.Runtime.RunId, config.StepId, update),
                cancellationToken
            );
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
                .WithoutToolInvocations(config.StepId)
                .WithoutUsage(config.StepId)
                .WithoutProfile(config.StepId),
        };
    }

    private PipelineRuntime ApplyPreInvocationPolicies(PipelineMessage<TState> message)
    {
        var runtime = message.Runtime;
        if (!config.ContinueSession)
        {
            runtime = runtime
                .WithoutSession(config.StepId)
                .WithoutToolInvocations(config.StepId)
                .WithoutUsage(config.StepId);
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
            runtime = runtime
                .WithoutSession(config.StepId)
                .WithoutToolInvocations(config.StepId)
                .WithoutUsage(config.StepId);
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

    private IReadOnlyList<ToolInvocationObservationDescriptor> RestoreToolInvocations(
        PipelineRuntime runtime
    ) =>
        runtime.AgentToolInvocations.TryGetValue(config.StepId, out var serialized)
            ? serialized
                .Deserialize<PersistedToolInvocation[]>()!
                .Select(invocation => invocation.ToDescriptor())
                .ToArray()
            : [];

    private PipelineRuntime CaptureToolInvocations(
        PipelineRuntime runtime,
        ToolOutcomeCollector collector
    ) =>
        runtime.WithToolInvocations(
            config.StepId,
            JsonSerializer.SerializeToElement(
                collector.ToolInvocations.Select(PersistedToolInvocation.FromDescriptor).ToArray()
            )
        );

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

internal sealed record PersistedToolInvocation(
    string Name,
    ToolEffect? Effect,
    ToolEvidence? Evidence,
    JsonElement Arguments,
    ToolInvocationStatus Status,
    PersistedProcessEvidence? Process
)
{
    internal static PersistedToolInvocation FromDescriptor(
        ToolInvocationObservationDescriptor invocation
    ) =>
        new(
            invocation.Name,
            invocation.Semantics?.Effect,
            invocation.Semantics?.Evidence,
            invocation.Arguments,
            invocation.Status,
            invocation.Result is ToolResultEvidenceDescriptor.Process process
                ? new PersistedProcessEvidence(
                    process.ExitCode,
                    process.Stdout,
                    process.Stderr,
                    process.Duration,
                    process.TimedOut,
                    process.Truncated
                )
                : null
        );

    internal ToolInvocationObservationDescriptor ToDescriptor() =>
        new(
            Name,
            Effect is { } effect ? new ToolSemantics(effect, Evidence ?? ToolEvidence.None) : null,
            Arguments,
            Status,
            Process is { } process
                ? new ToolResultEvidenceDescriptor.Process(
                    process.ExitCode,
                    process.Stdout,
                    process.Stderr,
                    process.Duration,
                    process.TimedOut,
                    process.Truncated
                )
                : null
        );
}

internal sealed record PersistedProcessEvidence(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut,
    bool Truncated
);

internal sealed class ToolOutcomeCollector
{
    internal readonly record struct ToolInvocationReservation(int Ordinal);

    private readonly object _sync = new();
    private string? _lifecycleToolName;
    private readonly Dictionary<
        string,
        (int Ordinal, ToolObservationDescriptor? Observation)
    > _latestToolOutcomes = [];
    private readonly List<ToolInvocationObservationDescriptor?> _toolInvocations = [];

    public ToolOutcomeCollector(
        IReadOnlyList<ToolInvocationObservationDescriptor>? priorInvocations = null
    )
    {
        if (priorInvocations is not null)
        {
            _toolInvocations.AddRange(priorInvocations);
        }
    }

    public bool HasLifecycleCall
    {
        get
        {
            lock (_sync)
            {
                return _lifecycleToolName is not null;
            }
        }
    }

    public void RecordLifecycleCall(string toolName)
    {
        lock (_sync)
        {
            _lifecycleToolName ??= toolName;
        }
    }

    public void RecordSuccessfulToolCall(
        ToolInvocationReservation reservation,
        ToolObservationDescriptor observation
    )
    {
        lock (_sync)
        {
            RecordToolOutcome(reservation, observation.Name, observation);
        }
    }

    public void RecordFailedToolCall(ToolInvocationReservation reservation, string toolName)
    {
        lock (_sync)
        {
            RecordToolOutcome(reservation, toolName, null);
        }
    }

    private void RecordToolOutcome(
        ToolInvocationReservation reservation,
        string toolName,
        ToolObservationDescriptor? observation
    )
    {
        if (
            !_latestToolOutcomes.TryGetValue(toolName, out var latest)
            || reservation.Ordinal > latest.Ordinal
        )
        {
            _latestToolOutcomes[toolName] = (reservation.Ordinal, observation);
        }
    }

    public ToolInvocationReservation ReserveToolInvocation()
    {
        lock (_sync)
        {
            var reservation = new ToolInvocationReservation(_toolInvocations.Count);
            _toolInvocations.Add(null);
            return reservation;
        }
    }

    public void CompleteToolInvocation(
        ToolInvocationReservation reservation,
        ToolInvocationObservationDescriptor observation
    )
    {
        lock (_sync)
        {
            _toolInvocations[reservation.Ordinal] = observation;
        }
    }

    public IReadOnlySet<ToolObservationDescriptor> SuccessfulTools
    {
        get
        {
            lock (_sync)
            {
                return _latestToolOutcomes
                    .Values.Select(value => value.Observation)
                    .Where(observation => observation is not null)
                    .Select(observation => observation!)
                    .ToHashSet();
            }
        }
    }

    public IReadOnlyList<ToolInvocationObservationDescriptor> ToolInvocations
    {
        get
        {
            lock (_sync)
            {
                return _toolInvocations
                    .Where(observation => observation is not null)
                    .Select(observation => observation!)
                    .ToArray();
            }
        }
    }

    public string? LifecycleToolName
    {
        get
        {
            lock (_sync)
            {
                return _lifecycleToolName;
            }
        }
    }
}

#pragma warning restore MAAI001
