using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

internal sealed partial class AgentBlock<TState>(
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
}
