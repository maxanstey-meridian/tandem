using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

internal sealed partial class AgentBlock<TState>
{
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

    private IChatClient SelectChatClient(PipelineMessage<TState> message) =>
        new ToolResultAdjacencyChatClient(
            chatClientFactory is null
                ? chatClient
                : chatClientFactory(
                    message.Runtime.Step(config.StepId).Profile?.ProfileName ?? config.ProfileName
                )
        );
}
