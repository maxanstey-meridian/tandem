using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Lifecycle;

#pragma warning disable MAAI001

namespace Tandem.Infrastructure.Blocks;

public sealed class AgentBlock<TState>(
    AgentBlockConfig<TState> config,
    IChatClient chatClient,
    string tandemHome,
    string? tandemExePath = null,
    Action<string, Guid, AgentResponseUpdate>? onUpdate = null,
    ToolInterceptor<TState>? toolInterceptor = null,
    Action<ChatOptions>? configureChatOptions = null
) : Executor<PipelineMessage<TState>, PipelineMessage<TState>>(config.BlockId)
{
    private static readonly TimeSpan _turnTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _mcpCallTimeout = TimeSpan.FromSeconds(30);

    private readonly LifecycleReceiptStore _receiptStore = new(tandemHome);
    private readonly string _tandemExePath =
        tandemExePath
        ?? Environment.ProcessPath
        ?? throw new InvalidOperationException("Process path unavailable.");

    public override async ValueTask<PipelineMessage<TState>> HandleAsync(
        PipelineMessage<TState> message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var blockSw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_turnTimeout);

        var runtime = message.Runtime;
        var invocationId = runtime.NextInvocationId(config.BlockId);

        var existingReceipt = await _receiptStore.ReadAsync(runtime.RunId, invocationId, cts.Token);
        if (existingReceipt is not null)
        {
            blockSw.Stop();
            return ApplyAcceptedReceipt(runtime, message.State, existingReceipt, blockSw.Elapsed);
        }

        var isCheckpointOnly = ShouldRunCheckpointOnly(runtime);

        var lifecycleTools = Array.Empty<AITool>();
        LifecycleMcpClient? mcpClient = null;
        if (isCheckpointOnly)
        {
            mcpClient = new LifecycleMcpClient(
                tandemHome,
                _tandemExePath,
                runtime.RunId,
                config.BlockId,
                invocationId,
                config.McpServerName
            );
            lifecycleTools = (
                await mcpClient.ListToolsAsync([config.Checkpoint!.ToolName], cts.Token)
            ).ToArray();
        }
        else if (config.LifecycleToolNames.Count > 0)
        {
            mcpClient = new LifecycleMcpClient(
                tandemHome,
                _tandemExePath,
                runtime.RunId,
                config.BlockId,
                invocationId,
                config.McpServerName
            );
            lifecycleTools = (
                await mcpClient.ListToolsAsync(config.LifecycleToolNames, cts.Token)
            ).ToArray();
        }

        try
        {
            var collector = new ToolOutcomeCollector();

            var fileStore = new GitExcludedFileStore(
                new BomlessFileSystemAgentFileStore(config.WorkspacePath(message.State))
            );

            var instructions = isCheckpointOnly
                ? config.Checkpoint!.Instructions
                : config.SystemInstructions;
            var tools = lifecycleTools.ToList();
            var agent = CreateAgent(
                fileStore,
                instructions,
                tools,
                message,
                isCheckpointOnly,
                requiredToolName: null,
                collector: collector
            );
            var session = await RestoreOrCreateSessionAsync(agent, runtime, cts.Token);
            var baseMessage = isCheckpointOnly
                ? config.Checkpoint!.UserMessage(message)
                : config.UserMessage(message);

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
            StructuredOutputResult<TState>? structuredResult = null;
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

                    onUpdate?.Invoke(config.BlockId, runtime.RunId, update);
                }

                turnSw.Stop();
                inputTokens = (inputTokens ?? 0) + (turnInputTokens ?? 0);
                outputTokens = (outputTokens ?? 0) + (turnOutputTokens ?? 0);
                lastModelCallDuration += turnSw.Elapsed;
                foreach (var toolName in collector.SuccessfulToolNames)
                {
                    structuredToolNames.Add(toolName);
                }

                if (config.StructuredOutput is not null)
                {
                    structuredResult = config.StructuredOutput(turnText.ToString(), message);
                    if (config.StructuredOutputAcceptance is not null)
                    {
                        var problems = config.StructuredOutputAcceptance(
                            new StructuredOutputAcceptanceObservation<TState>(
                                message,
                                structuredResult,
                                structuredToolNames,
                                structuredAttempt
                            )
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
                            config.StructuredOutputCorrectionRequiredToolName
                        )
                    )
                    {
                        agent = CreateAgent(
                            fileStore,
                            instructions,
                            tools,
                            message,
                            isCheckpointOnly,
                            config.StructuredOutputCorrectionRequiredToolName,
                            collector,
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
                    new AgentTurnObservation<TState>(
                        message,
                        turnText.ToString(),
                        turnToolNames,
                        collector.HasLifecycleCall,
                        continuationAttempt
                    ),
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
                    fileStore,
                    instructions,
                    tools,
                    message,
                    isCheckpointOnly,
                    directive.RequiredToolName,
                    collector
                );
            }

            var agentUsage = ResolveUsage(inputTokens, outputTokens, lastModelCallDuration);
            var runtimeAfterUsage = runtime.WithUsage(config.BlockId, agentUsage);

            var updatedRuntime = await PersistSessionAsync(
                agent,
                session,
                runtimeAfterUsage,
                cts.Token
            );

            var outcome = await ResolveOutcomeAsync(
                collector,
                structuredResult,
                runtime.RunId,
                invocationId,
                updatedRuntime,
                message.State,
                isCheckpointOnly,
                policyExhausted,
                continuationAttempt,
                cts.Token
            );
            blockSw.Stop();
            if (outcome.LatestOutcome is null)
            {
                return outcome;
            }
            var timedOutcome = outcome.LatestOutcome with { Duration = blockSw.Elapsed };
            return outcome with { LatestOutcome = timedOutcome };
        }
        finally
        {
            if (mcpClient is not null)
            {
                await mcpClient.DisposeAsync();
            }
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

    private void ConfigureFunctionInvocation(
        HarnessAgent agent,
        ToolOutcomeCollector collector,
        PipelineMessage<TState> message
    )
    {
        var invokingClient =
            agent.GetService<FunctionInvokingChatClient>()
            ?? throw new InvalidOperationException(
                "HarnessAgent did not expose its FunctionInvokingChatClient."
            );

        invokingClient.FunctionInvoker = async (ficContext, ct) =>
        {
            var isLifecycle =
                config.LifecycleToolNames.Contains(ficContext.Function.Name)
                || ficContext.Function.Name == "write_checkpoint";

            if (!isLifecycle && toolInterceptor is not null)
            {
                var interception = await toolInterceptor(message, ficContext, ct);
                if (interception is ToolInterceptionResult.Blocked blocked)
                {
                    return blocked.Message;
                }
            }

            if (!isLifecycle)
            {
                var toolResult = await ficContext.Function.InvokeAsync(ficContext.Arguments, ct);
                if (!IsToolError(toolResult))
                {
                    collector.RecordSuccessfulToolCall(ficContext.Function.Name);
                }
                return toolResult;
            }

            using var mcpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            mcpCts.CancelAfter(_mcpCallTimeout);
            var result = await ficContext.Function.InvokeAsync(ficContext.Arguments, mcpCts.Token);
            if (IsToolError(result))
            {
                return result;
            }
            collector.RecordLifecycleCall(ficContext.Function.Name);
            ficContext.Terminate = true;
            return result;
        };
    }

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
        ToolOutcomeCollector collector,
        StructuredOutputResult<TState>? structuredResult,
        Guid runId,
        string invocationId,
        PipelineRuntime runtime,
        TState state,
        bool isCheckpointOnly,
        bool policyExhausted,
        int continuationAttempt,
        CancellationToken ct
    )
    {
        if (isCheckpointOnly)
        {
            return ResolveCheckpointOutcomeAsync(
                collector,
                runId,
                invocationId,
                runtime,
                state,
                ct
            );
        }

        if (!collector.HasLifecycleCall)
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
            );
        }

        return ResolveLifecycleReceiptAsync(runId, invocationId, runtime, state, ct);
    }

    private async Task<PipelineMessage<TState>> ResolveCheckpointOutcomeAsync(
        ToolOutcomeCollector collector,
        Guid runId,
        string invocationId,
        PipelineRuntime runtime,
        TState state,
        CancellationToken ct
    )
    {
        if (!collector.HasLifecycleCall)
        {
            return new PipelineMessage<TState>(
                runtime.IncrementInvocations(config.BlockId),
                state,
                new BlockOutcome(
                    "agent.failed",
                    config.BlockId,
                    "Checkpoint-only mode: model did not call write_checkpoint.",
                    EmptyPayload()
                )
            );
        }

        var receipt = await _receiptStore.ReadAsync(runId, invocationId, ct);
        if (receipt is null)
        {
            return new PipelineMessage<TState>(
                runtime.IncrementInvocations(config.BlockId),
                state,
                new BlockOutcome(
                    "agent.failed",
                    config.BlockId,
                    "Checkpoint tool called but no receipt written.",
                    EmptyPayload()
                )
            );
        }

        return ApplyAcceptedReceipt(runtime, state, receipt);
    }

    private async Task<PipelineMessage<TState>> ResolveLifecycleReceiptAsync(
        Guid runId,
        string invocationId,
        PipelineRuntime runtime,
        TState state,
        CancellationToken ct
    )
    {
        var receipt = await _receiptStore.ReadAsync(runId, invocationId, ct);
        if (receipt is null)
        {
            return new PipelineMessage<TState>(
                runtime.IncrementInvocations(config.BlockId),
                state,
                new BlockOutcome(
                    "agent.failed",
                    config.BlockId,
                    "Lifecycle tool called but no receipt written.",
                    EmptyPayload()
                )
            );
        }

        return ApplyAcceptedReceipt(runtime, state, receipt);
    }

    private HarnessAgent CreateAgent(
        AgentFileStore fileStore,
        string instructions,
        IReadOnlyList<AITool> tools,
        PipelineMessage<TState> message,
        bool isCheckpointOnly,
        string? requiredToolName,
        ToolOutcomeCollector collector,
        bool configureStructuredOutput = true
    )
    {
        var chatOptions = new ChatOptions { Instructions = instructions, Tools = tools.ToList() };
        if (!string.IsNullOrWhiteSpace(requiredToolName))
        {
            chatOptions.ToolMode = ChatToolMode.RequireSpecific(requiredToolName);
        }
        if (configureStructuredOutput)
        {
            configureChatOptions?.Invoke(chatOptions);
        }

        var agent = new HarnessAgent(
            chatClient,
            new HarnessAgentOptions
            {
                Id = config.BlockId,
                Name = config.BlockId,
                HarnessInstructions = TandemHarnessInstructions.Value,
                ChatOptions = chatOptions,
                DisableFileMemory = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableAgentSkillsProvider = true,
                DisableWebSearch = true,
                DisableToolAutoApproval = true,
                DisableOpenTelemetry = true,
                DisableCompaction = true,
                MaximumIterationsPerRequest = 999,
                FileAccessStore = fileStore,
                FileAccessProviderOptions = ResolveAccessOptions(message, isCheckpointOnly),
            }
        );

        ConfigureFunctionInvocation(agent, collector, message);
        return agent;
    }

    private FileAccessProviderOptions ResolveAccessOptions(
        PipelineMessage<TState> message,
        bool isCheckpointOnly
    )
    {
        if (isCheckpointOnly)
        {
            return new FileAccessProviderOptions
            {
                DisableWriteTools = false,
                DisableReadOnlyToolApproval = true,
                DisableWriteToolApproval = true,
            };
        }

        // When a tool interceptor is configured, write tools stay visible —
        // the interceptor enforces the gate by blocking and returning a message.
        if (toolInterceptor is not null)
        {
            return new FileAccessProviderOptions
            {
                DisableWriteTools = false,
                DisableReadOnlyToolApproval = true,
                DisableWriteToolApproval = true,
            };
        }

        var allowMutation = config.AllowMutation(message.State);

        return new FileAccessProviderOptions
        {
            DisableWriteTools = !allowMutation,
            DisableReadOnlyToolApproval = true,
            DisableWriteToolApproval = true,
        };
    }

    private static JsonElement EmptyPayload() => JsonSerializer.SerializeToElement(new { });

    private TState ApplyReceipt(TState state, string kind, JsonElement payload) =>
        config.ReceiptTransition is null ? state : config.ReceiptTransition(state, kind, payload);

    private PipelineMessage<TState> ApplyAcceptedReceipt(
        PipelineRuntime runtime,
        TState state,
        LifecycleReceipt receipt,
        TimeSpan duration = default
    )
    {
        var isCheckpoint =
            config.Checkpoint is { } checkpoint && receipt.Kind == checkpoint.OutcomeKind;
        var updatedRuntime = isCheckpoint
            ? runtime.WithoutSession(config.BlockId).WithoutUsage(config.BlockId)
            : runtime;
        var updatedState = isCheckpoint
            ? config.Checkpoint!.Transition(state, receipt.Kind, receipt.Payload)
            : ApplyReceipt(state, receipt.Kind, receipt.Payload);

        return new PipelineMessage<TState>(
            updatedRuntime.IncrementInvocations(config.BlockId),
            updatedState,
            new BlockOutcome(
                receipt.Kind,
                config.BlockId,
                receipt.Summary,
                receipt.Payload,
                duration
            )
        );
    }

    private async Task<PipelineRuntime> PersistSessionAsync(
        HarnessAgent agent,
        AgentSession session,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        var json = JsonSerializer.Serialize(serialized);

        var sessionPath = GetSessionPath(runtime.RunId, config.BlockId);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(sessionPath, json, ct);

        return runtime.WithSession(
            config.BlockId,
            JsonSerializer.SerializeToElement(new { stored = true })
        );
    }

    private async Task<AgentSession> RestoreOrCreateSessionAsync(
        HarnessAgent agent,
        PipelineRuntime runtime,
        CancellationToken ct
    )
    {
        var sessionPath = GetSessionPath(runtime.RunId, config.BlockId);
        if (runtime.AgentSessions.ContainsKey(config.BlockId) && File.Exists(sessionPath))
        {
            var json = await File.ReadAllTextAsync(sessionPath, ct);
            var serialized = JsonSerializer.Deserialize<JsonElement>(json);
            return await agent.DeserializeSessionAsync(serialized, cancellationToken: ct);
        }

        return await agent.CreateSessionAsync(ct);
    }

    private string GetSessionPath(Guid runId, string blockId) =>
        Path.Combine(tandemHome, "runs", runId.ToString("N"), "sessions", $"{blockId}.json");
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
