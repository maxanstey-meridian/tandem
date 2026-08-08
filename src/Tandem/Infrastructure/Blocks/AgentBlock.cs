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
        config.BlockId,
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
        var invocationId = runtime.NextInvocationId(config.BlockId);
        var isCheckpointOnly = ShouldRunCheckpointOnly(runtime);
        var selectedCapabilities = isCheckpointOnly
            ? new[] { config.Checkpoint!.Capability }
            : config.Capabilities;
        var capabilityInvocation = new CapabilityInvocationState<TState>(
            runtime.RunId,
            config.BlockId,
            invocationId,
            message.State
        );
        var capabilityFunctions = selectedCapabilities
            .Select(capability => capability.Bind(capabilityInvocation))
            .ToArray();
        if (
            config.WorkspacePath is not null
            && capabilityFunctions.Any(tool =>
                tool.Name.StartsWith("file_access_", StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidOperationException(
                $"Agent '{config.BlockId}' has a capability that collides with a Harness file tool."
            );
        }

        {
            var collector = new ToolOutcomeCollector();

            var instructions = isCheckpointOnly
                ? config.Checkpoint!.Instructions
                : config.SystemInstructions;
            var tools = capabilityFunctions.Cast<AITool>().ToList();
            var boundCapabilityNames = capabilityFunctions
                .Select(function => function.Name)
                .ToHashSet(StringComparer.Ordinal);
            var agent = CreateAgent(
                instructions,
                tools,
                message,
                isCheckpointOnly,
                requiredToolName: null,
                collector: collector,
                boundCapabilityNames: boundCapabilityNames
            );
            var session = await RestoreOrCreateSessionAsync(agent, runtime, cts.Token);
            var baseMessage = isCheckpointOnly
                ? config.Checkpoint!.UserMessage(
                    message.State,
                    runtime.AgentUsage.GetValueOrDefault(config.BlockId)?.CurrentContextTokens ?? 0
                )
                : config.ContextUserMessage?.Invoke(message) ?? config.UserMessage!(message.State);

            var augmentation = config.MessageAugmentation is not null
                ? await config.MessageAugmentation(message, cts.Token)
                : null;
            var userMessage = augmentation is not null
                ? $"{baseMessage}\n\n{augmentation}"
                : baseMessage;

            long? inputTokens = null;
            long? outputTokens = null;
            var lastModelCallDuration = TimeSpan.Zero;
            var continuationAttempt = 0;
            var policyExhausted = false;
            var structuredAttempt = 0;
            AgentStructuredOutputResult<TState>? structuredResult = null;
            var structuredToolNames = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                var turnText = new StringBuilder();
                var turnToolNames = new List<string>();
                var turnInputTokens = default(long?);
                var turnOutputTokens = default(long?);
                var turnSw = Stopwatch.StartNew();

                await foreach (
                    var update in agent.RunStreamingAsync(userMessage, session, null, cts.Token)
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
                inputTokens = (inputTokens ?? 0) + (turnInputTokens ?? 0);
                outputTokens = (outputTokens ?? 0) + (turnOutputTokens ?? 0);
                lastModelCallDuration += turnSw.Elapsed;
                foreach (var toolName in collector.SuccessfulToolNames)
                {
                    structuredToolNames.Add(toolName);
                }

                if (capabilityInvocation.Accepted is not null)
                {
                    break;
                }

                if (config.StructuredOutput is not null)
                {
                    structuredResult = config.StructuredOutput.Parse(
                        turnText.ToString(),
                        message.State
                    );
                    if (config.StructuredOutput.Accept is not null)
                    {
                        var problems = config.StructuredOutput.Accept(
                            message,
                            structuredResult,
                            structuredToolNames,
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
                            isCheckpointOnly,
                            config.StructuredOutput.CorrectionRequiredToolName,
                            collector,
                            boundCapabilityNames,
                            configureStructuredOutput: false
                        );
                    }
                    continue;
                }

                if (isCheckpointOnly || collector.HasLifecycleCall || config.TurnPolicy is null)
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
                    isCheckpointOnly,
                    directive.RequiredToolName,
                    collector,
                    boundCapabilityNames
                );
            }

            var agentUsage = ResolveUsage(inputTokens, outputTokens, lastModelCallDuration);
            var runtimeAfterUsage = runtime.WithUsage(config.BlockId, agentUsage);

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
                isCheckpointOnly,
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

    private bool ShouldRunCheckpointOnly(PipelineRuntime runtime)
    {
        if (config.Checkpoint is not { } policy)
        {
            return false;
        }

        if (!runtime.AgentUsage.TryGetValue(config.BlockId, out var usage))
        {
            return false;
        }

        return usage.CurrentContextTokens + policy.MaxOutputTokens >= policy.CheckpointAtTokens;
    }

    private AgentUsage ResolveUsage(long? inputTokens, long? outputTokens, TimeSpan elapsed)
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
            LastModelCallDuration: elapsed
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
                    var classified = toolEffects.TryGet(ficContext.Function.Name, out var effect);

                    if (toolInterceptor is not null)
                    {
                        var blockedMessage = await toolInterceptor(
                            message,
                            ficContext.Function.Name,
                            classified ? effect : null,
                            ct
                        );
                        if (blockedMessage is not null)
                        {
                            return blockedMessage;
                        }
                    }

                    var result = await next(ficContext, ct);
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
                        collector.RecordSuccessfulToolCall(ficContext.Function.Name);
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
        bool isCheckpointOnly,
        bool policyExhausted,
        int continuationAttempt,
        PipelineRunContext? runContext
    )
    {
        var state = capabilityInvocation.State;
        if (isCheckpointOnly)
        {
            return Task.FromResult(
                capabilityInvocation.Accepted is { } checkpoint
                    ? ApplyAcceptedCapability(runtime, checkpoint, resetSession: true, runContext)
                    : new PipelineMessage<TState>(
                        runtime.IncrementInvocations(config.BlockId),
                        state,
                        new BlockOutcome(
                            "agent.failed",
                            config.BlockId,
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
                            runtime.IncrementInvocations(config.BlockId),
                            state,
                            new BlockOutcome(
                                "agent.failed",
                                config.BlockId,
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
                        runtime.IncrementInvocations(config.BlockId),
                        outcomeState,
                        new BlockOutcome(
                            structured.Kind,
                            config.BlockId,
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
                    runtime.IncrementInvocations(config.BlockId),
                    state,
                    new BlockOutcome(kind, config.BlockId, summary, payload)
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
        var updatedRuntime = resetSession
            ? runtime.WithoutSession(config.BlockId).WithoutUsage(config.BlockId)
            : runtime;
        return new PipelineMessage<TState>(
            updatedRuntime.IncrementInvocations(config.BlockId),
            accepted.State,
            new BlockOutcome(
                accepted.CapabilityId,
                config.BlockId,
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
        bool isCheckpointOnly,
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
                message.Runtime.AgentProfiles.GetValueOrDefault(config.BlockId)?.ProfileName
                    ?? config.ProfileName
            );
        var toolEffects = new ToolEffectRegistry();
        foreach (var capabilityName in boundCapabilityNames)
        {
            toolEffects.Add(capabilityName, ToolEffect.LifecycleTransition);
        }
        var allowMutation = config.AllowMutation?.Invoke(message.State) ?? false;
        var implementationContext = new AgentImplementationContext(
            config.BlockId,
            selectedChatClient,
            chatOptions,
            config.WorkspacePath?.Invoke(message.State),
            !isCheckpointOnly && (allowMutation || toolInterceptor is not null),
            toolEffects
        );
        var agent = config.ImplementationFactory is null
            ? new ChatClientAgent(
                selectedChatClient,
                new ChatClientAgentOptions
                {
                    Id = config.BlockId,
                    Name = config.BlockId,
                    ChatOptions = chatOptions,
                }
            )
            : config.ImplementationFactory(implementationContext);

        return ConfigureFunctionInvocation(
            agent,
            collector,
            message,
            boundCapabilityNames,
            toolEffects
        );
    }

    private static JsonElement EmptyPayload() => JsonSerializer.SerializeToElement(new { });

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
                onUpdate?.Invoke(config.BlockId, runId, semantic);
                if (message.RunContext is not null)
                {
                    await message.RunContext.ObserveAsync(
                        new PipelineAgentUpdated(runId, config.BlockId, semantic),
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
                .Runtime.WithoutSession(config.BlockId)
                .WithoutUsage(config.BlockId)
                .WithoutProfile(config.BlockId),
        };
    }

    private PipelineRuntime ApplyPreInvocationPolicies(PipelineMessage<TState> message)
    {
        var runtime = message.Runtime;
        if (!config.ContinueSession)
        {
            runtime = runtime.WithoutSession(config.BlockId).WithoutUsage(config.BlockId);
        }

        var profile =
            config.ProfilePolicy?.Invoke(message.State)
            ?? new AgentProfileSelection(config.ProfileName, "Configured agent profile.");
        if (
            runtime.AgentProfiles.TryGetValue(config.BlockId, out var currentProfile)
            && !string.Equals(
                currentProfile.ProfileName,
                profile.ProfileName,
                StringComparison.Ordinal
            )
        )
        {
            runtime = runtime.WithoutSession(config.BlockId).WithoutUsage(config.BlockId);
        }
        return runtime.WithProfile(config.BlockId, profile);
    }

    private async Task<PipelineRuntime> CaptureSessionAsync(
        AIAgent agent,
        AgentSession session,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        return runtime.WithSession(config.BlockId, serialized);
    }

    private async Task<AgentSession> RestoreOrCreateSessionAsync(
        AIAgent agent,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        if (runtime.AgentSessions.TryGetValue(config.BlockId, out var serialized))
        {
            return await agent.DeserializeSessionAsync(serialized, cancellationToken: ct);
        }

        return await agent.CreateSessionAsync(ct);
    }
}

internal sealed class ToolOutcomeCollector
{
    private string? _lifecycleToolName;
    private readonly HashSet<string> _successfulToolNames = new(StringComparer.Ordinal);

    public bool HasLifecycleCall => _lifecycleToolName is not null;

    public void RecordLifecycleCall(string toolName) => _lifecycleToolName ??= toolName;

    public void RecordSuccessfulToolCall(string toolName) => _successfulToolNames.Add(toolName);

    public IReadOnlySet<string> SuccessfulToolNames => _successfulToolNames;

    public string? LifecycleToolName => _lifecycleToolName;
}

#pragma warning restore MAAI001
