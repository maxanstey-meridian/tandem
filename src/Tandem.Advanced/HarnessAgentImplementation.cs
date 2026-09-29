using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class HarnessAgentImplementation
{
    internal static AIAgent Create(AgentImplementationContext context, string harnessInstructions)
    {
        var workspace = context.Workspace;
        var providers =
            context.Skills.Count == 0
                ? new List<AIContextProvider>()
                : [AgentSkillRuntime.CreateProvider(context.Skills)];
        if (workspace is not null)
        {
            var mafFileToolNames = HarnessTools.AddFileTools(
                context.ChatOptions,
                context.ToolEffects,
                workspace
            );
            if (mafFileToolNames.Count > 0)
            {
                providers.Add(
                    new FilteringAIContextProvider(
                        new FileAccessProvider(
                            new WorkspaceFileStore(workspace.Path),
                            new FileAccessProviderOptions
                            {
                                DisableWriteTools = !workspace.FileTools.Any(
                                    HarnessTools.IsMutation
                                ),
                                DisableReadOnlyToolApproval = true,
                                DisableWriteToolApproval = true,
                            }
                        ),
                        mafFileToolNames
                    )
                );
            }
            var existingToolCount = context.ChatOptions.Tools?.Count ?? 0;
            if (workspace.IncludeGitReadOnly)
            {
                ReadOnlyGitTools.Add(context.ChatOptions, workspace.Path, context.ToolEffects);
            }
            WorkspaceShellTools.Add(context.ChatOptions, workspace, context.ToolEffects);
            var workspaceTools = context.ChatOptions.Tools?.Skip(existingToolCount).ToArray() ?? [];
            if (workspaceTools.Length > 0)
            {
                context.ChatOptions.Tools = context
                    .ChatOptions.Tools?.Take(existingToolCount)
                    .ToList();
                providers.Add(new StaticToolsAIContextProvider(workspaceTools));
            }
            RegisteredWorkspaceTools.Add(context.ChatOptions, workspace, context.ToolEffects);
        }
        TavilyWebTools.Add(
            context,
            Environment.GetEnvironmentVariable(TavilyWebTools.ApiKeyEnvironmentVariable)
        );
        return new HarnessAgent(
            context.ChatClient,
            CreateOptions(context, harnessInstructions, providers)
        );
    }

    internal static HarnessAgentOptions CreateOptions(
        AgentImplementationContext context,
        string harnessInstructions,
        IReadOnlyList<AIContextProvider>? providers = null
    ) =>
        new()
        {
            Id = context.Id,
            Name = context.Id,
            HarnessInstructions = harnessInstructions,
            ChatOptions = context.ChatOptions,
            DisableFileMemory = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableAgentSkillsProvider = true,
            AIContextProviders = providers is { Count: > 0 } ? providers : null,
            DisableWebSearch = true,
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true,
            MaxContextWindowTokens = context.MaxContextWindowTokens,
            MaxOutputTokens = context.MaxOutputTokens,
            DisableCompaction =
                context.DisableCompaction
                || context.MaxContextWindowTokens is null
                || context.MaxOutputTokens is null,
            CompactionStrategy =
                !context.DisableCompaction
                && context.MaxContextWindowTokens is { } ctx
                && context.MaxOutputTokens is { } output
                    ? BuildCompactionStrategy(ctx, output)
                    : null,
            MaximumIterationsPerRequest = 40,
            FileAccessStore = null,
        };

    private static CompactionStrategy BuildCompactionStrategy(
        int maxContextWindowTokens,
        int maxOutputTokens
    )
    {
        var inputBudget = maxContextWindowTokens - maxOutputTokens;
        return new PipelineCompactionStrategy(
            new ToolResultCompactionStrategy(
                CompactionTriggers.TokensExceed((int)(inputBudget * 0.5)),
                minimumPreservedGroups: 10
            )
            {
                ToolCallFormatter = _ => "[prior tool results compacted — consult the ledger]",
            },
            new TruncationCompactionStrategy(
                CompactionTriggers.TokensExceed((int)(inputBudget * 0.8)),
                minimumPreservedGroups: 10
            )
        );
    }
}

internal sealed class FilteringAIContextProvider(
    AIContextProvider inner,
    IReadOnlySet<string> selectedToolNames
) : AIContextProvider
{
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken
    )
    {
        var existingTools = context.AIContext.Tools?.ToArray() ?? [];
        var result = await inner.InvokingAsync(context, cancellationToken);
        result.Tools =
        [
            .. existingTools,
            .. (result.Tools ?? []).Where(tool =>
                selectedToolNames.Contains(tool.Name)
                && existingTools.All(existing => existing.Name != tool.Name)
            ),
        ];
        return result;
    }

    protected override ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken
    ) => inner.InvokedAsync(context, cancellationToken);
}

internal sealed class StaticToolsAIContextProvider(IReadOnlyList<AITool> tools) : AIContextProvider
{
    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken
    )
    {
        context.AIContext.Tools = [.. context.AIContext.Tools ?? [], .. tools];
        return ValueTask.FromResult(context.AIContext);
    }
}

#pragma warning restore MAAI001
