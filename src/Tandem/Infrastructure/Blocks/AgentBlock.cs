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

public sealed class AgentBlock : Executor<PipelineMessage, PipelineMessage>
{
    private static readonly TimeSpan _turnTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _mcpCallTimeout = TimeSpan.FromSeconds(30);

    private readonly AgentBlockConfig _config;
    private readonly IChatClient _chatClient;
    private readonly LifecycleReceiptStore _receiptStore;
    private readonly string _tandemHome;
    private readonly string _tandemExePath;
    private readonly Action<AgentResponseUpdate>? _onUpdate;

    public AgentBlock(
        AgentBlockConfig config,
        IChatClient chatClient,
        string tandemHome,
        string? tandemExePath = null,
        Action<AgentResponseUpdate>? onUpdate = null
    )
        : base(config.BlockId)
    {
        _config = config;
        _chatClient = chatClient;
        _receiptStore = new LifecycleReceiptStore(tandemHome);
        _tandemHome = tandemHome;
        _tandemExePath =
            tandemExePath
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Process path unavailable.");
        _onUpdate = onUpdate;
    }

    public override async ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_turnTimeout);

        var ctx = message.Context;
        var invocationId = ctx.NextInvocationId(_config.BlockId);

        var existingReceipt = await _receiptStore.ReadAsync(ctx.RunId, invocationId, cts.Token);
        if (existingReceipt is not null)
        {
            return new PipelineMessage(
                ctx.IncrementInvocations(_config.BlockId),
                new BlockOutcome(
                    existingReceipt.Kind,
                    _config.BlockId,
                    existingReceipt.Summary,
                    existingReceipt.Payload
                )
            );
        }

        var isCheckpointOnly = ShouldRunCheckpointOnly(ctx);

        var lifecycleTools = Array.Empty<AITool>();
        LifecycleMcpClient? mcpClient = null;
        if (isCheckpointOnly)
        {
            mcpClient = new LifecycleMcpClient(
                _tandemHome,
                _tandemExePath,
                ctx.RunId,
                _config.BlockId,
                invocationId
            );
            lifecycleTools = (
                await mcpClient.ListToolsAsync(["write_checkpoint"], cts.Token)
            ).ToArray();
        }
        else if (_config.LifecycleToolNames.Count > 0)
        {
            mcpClient = new LifecycleMcpClient(
                _tandemHome,
                _tandemExePath,
                ctx.RunId,
                _config.BlockId,
                invocationId
            );
            lifecycleTools = (
                await mcpClient.ListToolsAsync(_config.LifecycleToolNames, cts.Token)
            ).ToArray();
        }

        try
        {
            var collector = new LifecycleOutcomeCollector();
            var invokingClient = WrapWithFunctionInvocation(_chatClient, collector);

            var fileStore = new GitExcludedFileStore(
                new FileSystemAgentFileStore(ctx.WorkspacePath)
            );

            var instructions = isCheckpointOnly
                ? CheckpointOnlyInstructions
                : _config.SystemInstructions;

            var tools = lifecycleTools.ToList();

            var agent = new HarnessAgent(
                invokingClient,
                new HarnessAgentOptions
                {
                    Id = _config.BlockId,
                    Name = _config.BlockId,
                    HarnessInstructions = "",
                    ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools },
                    DisableFileMemory = true,
                    DisableTodoProvider = true,
                    DisableAgentModeProvider = true,
                    DisableAgentSkillsProvider = true,
                    DisableWebSearch = true,
                    DisableToolAutoApproval = true,
                    DisableOpenTelemetry = true,
                    DisableCompaction = true,
                    MaximumIterationsPerRequest = 100,
                    FileAccessStore = fileStore,
                    FileAccessProviderOptions = ResolveAccessOptions(ctx, isCheckpointOnly),
                }
            );

            var session = await RestoreOrCreateSessionAsync(agent, ctx, cts.Token);
            var userMessage = isCheckpointOnly
                ? BuildCheckpointUserMessage(ctx)
                : BuildUserMessage(message);

            var assistantText = new StringBuilder();
            long? inputTokens = null;
            long? outputTokens = null;
            var modelCallSw = Stopwatch.StartNew();

            await foreach (
                var update in agent.RunStreamingAsync(userMessage, session, null, cts.Token)
            )
            {
                foreach (var content in update.Contents)
                {
                    if (content is TextContent text)
                    {
                        assistantText.Append(text.Text);
                    }
                    else if (content is UsageContent usageContent)
                    {
                        inputTokens = usageContent.Details.InputTokenCount;
                        outputTokens = usageContent.Details.OutputTokenCount;
                    }
                }

                _onUpdate?.Invoke(update);
            }

            modelCallSw.Stop();

            var agentUsage = ResolveUsage(inputTokens, outputTokens, modelCallSw.Elapsed);
            var contextAfterUsage = ctx.WithUsage(_config.BlockId, agentUsage);

            var updatedContext = await PersistSessionAsync(
                agent,
                session,
                contextAfterUsage,
                cts.Token
            );

            var outcome = await ResolveOutcomeAsync(
                collector,
                assistantText.ToString(),
                ctx.RunId,
                invocationId,
                updatedContext,
                isCheckpointOnly,
                cts.Token
            );
            return outcome;
        }
        finally
        {
            if (mcpClient is not null)
            {
                await mcpClient.DisposeAsync();
            }
        }
    }

    private bool ShouldRunCheckpointOnly(PipelineContext ctx)
    {
        if (_config.Checkpoint is not { } policy)
        {
            return false;
        }

        if (!ctx.AgentUsage.TryGetValue(_config.BlockId, out var usage))
        {
            return false;
        }

        return usage.CurrentContextTokens + policy.MaxOutputTokens >= policy.CheckpointAtTokens;
    }

    private AgentUsage ResolveUsage(long? inputTokens, long? outputTokens, TimeSpan elapsed)
    {
        var policy = _config.Checkpoint;
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

    private IChatClient WrapWithFunctionInvocation(
        IChatClient inner,
        LifecycleOutcomeCollector collector
    )
    {
        return inner
            .AsBuilder()
            .UseFunctionInvocation(
                null,
                fic =>
                {
                    fic.FunctionInvoker = async (ficContext, ct) =>
                    {
                        var isLifecycle =
                            _config.LifecycleToolNames.Contains(ficContext.Function.Name)
                            || ficContext.Function.Name == "write_checkpoint";

                        if (!isLifecycle)
                        {
                            return await ficContext.Function.InvokeAsync(ficContext.Arguments, ct);
                        }

                        using var mcpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        mcpCts.CancelAfter(_mcpCallTimeout);
                        var result = await ficContext.Function.InvokeAsync(
                            ficContext.Arguments,
                            mcpCts.Token
                        );
                        collector.RecordLifecycleCall(ficContext.Function.Name);
                        ficContext.Terminate = true;
                        return result;
                    };
                }
            )
            .Build();
    }

    private Task<PipelineMessage> ResolveOutcomeAsync(
        LifecycleOutcomeCollector collector,
        string assistantText,
        Guid runId,
        string invocationId,
        PipelineContext ctx,
        bool isCheckpointOnly,
        CancellationToken ct
    )
    {
        if (isCheckpointOnly)
        {
            return ResolveCheckpointOutcomeAsync(collector, runId, invocationId, ctx, ct);
        }

        if (!collector.HasLifecycleCall)
        {
            if (_config.StructuredOutput is not null)
            {
                var structured = _config.StructuredOutput(assistantText, ctx);
                var outcomeCtx = structured.UpdatedContext ?? ctx;
                return Task.FromResult(
                    new PipelineMessage(
                        outcomeCtx.IncrementInvocations(_config.BlockId),
                        new BlockOutcome(
                            structured.Kind,
                            _config.BlockId,
                            structured.Summary,
                            structured.Payload
                        )
                    )
                );
            }

            return Task.FromResult(
                new PipelineMessage(
                    ctx.IncrementInvocations(_config.BlockId),
                    new BlockOutcome(
                        "agent.completed",
                        _config.BlockId,
                        "(no lifecycle call)",
                        EmptyPayload()
                    )
                )
            );
        }

        return ResolveLifecycleReceiptAsync(runId, invocationId, ctx, ct);
    }

    private async Task<PipelineMessage> ResolveCheckpointOutcomeAsync(
        LifecycleOutcomeCollector collector,
        Guid runId,
        string invocationId,
        PipelineContext ctx,
        CancellationToken ct
    )
    {
        if (!collector.HasLifecycleCall)
        {
            return new PipelineMessage(
                ctx.IncrementInvocations(_config.BlockId),
                new BlockOutcome(
                    "agent.failed",
                    _config.BlockId,
                    "Checkpoint-only mode: model did not call write_checkpoint.",
                    EmptyPayload()
                )
            );
        }

        var receipt = await _receiptStore.ReadAsync(runId, invocationId, ct);
        if (receipt is null)
        {
            return new PipelineMessage(
                ctx.IncrementInvocations(_config.BlockId),
                new BlockOutcome(
                    "agent.failed",
                    _config.BlockId,
                    "Checkpoint tool called but no receipt written.",
                    EmptyPayload()
                )
            );
        }

        // After checkpoint: retain the checkpoint payload, remove the executor
        // session and usage so the next invocation starts fresh.
        var clearedContext = ctx.WithoutSession(_config.BlockId).WithCheckpoint(receipt.Payload);

        clearedContext = clearedContext with
        {
            AgentUsage = clearedContext
                .AgentUsage.Where(kvp => kvp.Key != _config.BlockId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

        return new PipelineMessage(
            clearedContext.IncrementInvocations(_config.BlockId),
            new BlockOutcome(receipt.Kind, _config.BlockId, receipt.Summary, receipt.Payload)
        );
    }

    private async Task<PipelineMessage> ResolveLifecycleReceiptAsync(
        Guid runId,
        string invocationId,
        PipelineContext ctx,
        CancellationToken ct
    )
    {
        var receipt = await _receiptStore.ReadAsync(runId, invocationId, ct);
        if (receipt is null)
        {
            return new PipelineMessage(
                ctx.IncrementInvocations(_config.BlockId),
                new BlockOutcome(
                    "agent.failed",
                    _config.BlockId,
                    "Lifecycle tool called but no receipt written.",
                    EmptyPayload()
                )
            );
        }

        var updatedCtx = ctx;
        if (receipt.Kind == OutcomeKinds.ReportSubmitted)
        {
            updatedCtx = ctx.WithImplementationReport(receipt.Payload);
        }

        return new PipelineMessage(
            updatedCtx.IncrementInvocations(_config.BlockId),
            new BlockOutcome(receipt.Kind, _config.BlockId, receipt.Summary, receipt.Payload)
        );
    }

    private FileAccessProviderOptions ResolveAccessOptions(
        PipelineContext ctx,
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

        var allowMutation = _config.Access switch
        {
            WorkspaceAccess.ReadOnly => false,
            WorkspaceAccess.MutationGated => ctx.MutationAuthorized,
            _ => false,
        };

        return new FileAccessProviderOptions
        {
            DisableWriteTools = !allowMutation,
            DisableReadOnlyToolApproval = true,
            DisableWriteToolApproval = true,
        };
    }

    private static JsonElement EmptyPayload() => JsonSerializer.SerializeToElement(new { });

    private string BuildUserMessage(PipelineMessage message) =>
        _config.BlockId switch
        {
            BlockIds.Planner => BuildPlannerMessage(message),
            BlockIds.Reviewer => BuildReviewerMessage(message.Context),
            _ => BuildExecutorMessage(message.Context),
        };

    private static string BuildExecutorMessage(PipelineContext ctx)
    {
        var packet = ctx.Packet;
        var outcomes = string.Join(
            "\n",
            packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var constraints =
            packet.Constraints.Count > 0
                ? string.Join("\n", packet.Constraints.Select(c => $"- {c}"))
                : "(none)";

        var plannerSection = ctx.PlannerDecision is { } decision
            ? $"""

                Planner decision: {decision.Decision}
                Planner rationale: {decision.Rationale}
                Planner constraints:
                {string.Join(
                    "\n",
                    decision.Constraints.Count > 0
                        ? decision.Constraints.Select(c => $"- {c}")
                        : ["(none)"]
                )}
                """
            : "";

        var verificationSection =
            ctx.VerificationResults.Count > 0
                ? $"""

                    Latest verification failure (if any):
                    {FormatVerificationResults(ctx.VerificationResults)}
                    """
                : "";

        var candidateSection = ctx.CandidateSha is { } sha
            ? $"""

                Current candidate SHA: {sha}
                """
            : "";

        var checkpointSection = ctx.CheckpointPayload is { } checkpoint
            ? $"""

                Previous checkpoint (context was compacted, continue from here):
                {checkpoint.GetRawText()}
                """
            : "";

        return $"""
            Packet: {packet.Title}
            Workspace: {ctx.WorkspacePath}
            Pinned base: {ctx.PinnedBaseSha}
            Mutation authorized: {ctx.MutationAuthorized}

            Outcomes:
            {outcomes}

            Constraints:
            {constraints}{plannerSection}{verificationSection}{candidateSection}{checkpointSection}
            """;
    }

    private static string BuildPlannerMessage(PipelineMessage message)
    {
        var ctx = message.Context;
        var packet = ctx.Packet;
        var outcomes = string.Join(
            "\n",
            packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var constraints =
            packet.Constraints.Count > 0
                ? string.Join("\n", packet.Constraints.Select(c => $"- {c}"))
                : "(none)";

        // The executor's ask_planner payload is the latest outcome payload.
        var executorQuestion = "(no question provided)";
        var executorApproach = "(no approach provided)";
        var executorEvidence = "(no evidence provided)";

        if (message.LatestOutcome?.Payload is { } payload)
        {
            if (
                payload.TryGetProperty("question", out var q)
                && q.ValueKind == JsonValueKind.String
            )
            {
                executorQuestion = q.GetString() ?? executorQuestion;
            }
            if (
                payload.TryGetProperty("proposedApproach", out var a)
                && a.ValueKind == JsonValueKind.String
            )
            {
                executorApproach = a.GetString() ?? executorApproach;
            }
            if (
                payload.TryGetProperty("evidence", out var ev)
                && ev.ValueKind == JsonValueKind.Array
            )
            {
                executorEvidence = string.Join(
                    ", ",
                    ev.EnumerateArray().Select(e => e.GetString())
                );
            }
        }

        var previousConstraints =
            ctx.PlannerConstraints.Count > 0
                ? string.Join("\n", ctx.PlannerConstraints.Select(c => $"- {c}"))
                : "(none)";

        return $"""
            Packet: {packet.Title}
            Workspace: {ctx.WorkspacePath}

            Outcomes:
            {outcomes}

            Constraints:
            {constraints}

            Executor's question:
            {executorQuestion}

            Executor's proposed approach:
            {executorApproach}

            Executor's evidence:
            {executorEvidence}

            Previous planner constraints:
            {previousConstraints}

            Return a structured JSON decision: Proceed, ProceedWithConstraints, NeedsHuman, or Stop.
            """;
    }

    private static string BuildReviewerMessage(PipelineContext ctx)
    {
        var packet = ctx.Packet;
        var outcomes = string.Join(
            "\n",
            packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var constraints =
            ctx.PlannerConstraints.Count > 0
                ? string.Join("\n", ctx.PlannerConstraints.Select(c => $"- {c}"))
                : "(none)";

        var verification =
            ctx.VerificationResults.Count > 0
                ? FormatVerificationResults(ctx.VerificationResults)
                : "(no verification commands)";

        var candidate = ctx.CandidateSha ?? "(no candidate)";

        var reportSection = ctx.ImplementationReport is { } report
            ? $"""

                Implementation report:
                {report.GetRawText()}
                """
            : "";

        return $"""
            Packet: {packet.Title}
            Workspace: {ctx.WorkspacePath}
            Pinned base: {ctx.PinnedBaseSha}
            Candidate SHA: {candidate}

            Outcomes:
            {outcomes}

            Planner constraints:
            {constraints}

            Verification results:
            {verification}{reportSection}

            You may inspect changed files through your read-only tools.

            Return a structured JSON decision: Accept, RequestChanges, or NeedsHuman.
            """;
    }

    private string BuildCheckpointUserMessage(PipelineContext ctx)
    {
        var usage = ctx.AgentUsage.GetValueOrDefault(_config.BlockId);
        var contextTokens = usage?.CurrentContextTokens ?? 0;
        var checkpointAt = _config.Checkpoint?.CheckpointAtTokens ?? 0;

        return $"""
            Context window approaching limit: {contextTokens} tokens used, checkpoint threshold is {checkpointAt}.

            Write a checkpoint of your current work state using the write_checkpoint tool.
            Summarize what you have completed and what remains to be done.

            Call write_checkpoint now.
            """;
    }

    private static string FormatVerificationResults(IReadOnlyList<VerificationResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            var status = r.ExitCode == 0 ? "PASS" : "FAIL";
            sb.AppendLine(
                $"  [{status}] {r.Command} (exit {r.ExitCode}, {r.Elapsed.TotalMilliseconds:F0}ms)"
            );
            if (!string.IsNullOrWhiteSpace(r.Stdout))
            {
                sb.AppendLine($"    stdout: {Truncate(r.Stdout, 500)}");
            }
            if (!string.IsNullOrWhiteSpace(r.Stderr))
            {
                sb.AppendLine($"    stderr: {Truncate(r.Stderr, 500)}");
            }
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private async Task<PipelineContext> PersistSessionAsync(
        HarnessAgent agent,
        AgentSession session,
        PipelineContext ctx,
        CancellationToken ct
    )
    {
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        var json = JsonSerializer.Serialize(serialized);

        var sessionPath = GetSessionPath(ctx.RunId, _config.BlockId);
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(sessionPath, json, ct);

        return ctx.WithSession(
            _config.BlockId,
            JsonSerializer.SerializeToElement(new { stored = true })
        );
    }

    private async Task<AgentSession> RestoreOrCreateSessionAsync(
        HarnessAgent agent,
        PipelineContext ctx,
        CancellationToken ct
    )
    {
        var sessionPath = GetSessionPath(ctx.RunId, _config.BlockId);
        if (ctx.AgentSessions.ContainsKey(_config.BlockId) && File.Exists(sessionPath))
        {
            var json = await File.ReadAllTextAsync(sessionPath, ct);
            var serialized = JsonSerializer.Deserialize<JsonElement>(json);
            return await agent.DeserializeSessionAsync(serialized, cancellationToken: ct);
        }

        return await agent.CreateSessionAsync(ct);
    }

    private string GetSessionPath(Guid runId, string blockId) =>
        Path.Combine(_tandemHome, "runs", runId.ToString("N"), "sessions", $"{blockId}.json");

    private const string CheckpointOnlyInstructions = """
        You are Tandem's implementation block in checkpoint-only mode.

        Your context window is approaching its limit. You must write a checkpoint
        of your current work state using the write_checkpoint tool. Summarize
        what you have completed and what remains to be done next.

        This is the only action available. Do not attempt other work.
        """;
}

internal sealed class LifecycleOutcomeCollector
{
    private string? _lifecycleToolName;

    public bool HasLifecycleCall => _lifecycleToolName is not null;

    public void RecordLifecycleCall(string toolName) => _lifecycleToolName ??= toolName;

    public string? LifecycleToolName => _lifecycleToolName;
}

#pragma warning restore MAAI001
