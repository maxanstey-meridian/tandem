using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Infrastructure;

internal sealed record AgentImplementationContext(
    string Id,
    IChatClient ChatClient,
    ChatOptions ChatOptions,
    ResolvedAgentWorkspace? Workspace,
    ToolEffectRegistry ToolEffects,
    IReadOnlyList<AgentSkillDescriptor> Skills,
    int? MaxContextWindowTokens,
    int? MaxOutputTokens,
    bool DisableCompaction = false
);

internal sealed record ResolvedAgentWorkspace(
    string Path,
    IReadOnlySet<WorkspaceToolKind> FileTools,
    bool IncludeGitReadOnly,
    bool IncludeShell,
    bool IncludeWebSearch,
    bool IncludeWebFetch,
    IReadOnlyList<AgentCommandDescriptor> Commands,
    IReadOnlyList<AgentWorkspaceToolDescriptor>? RegisteredTools = null
);

internal enum WorkspaceToolKind
{
    ReadFile,
    ListFiles,
    Grep,
    WriteFile,
    DeleteFile,
    Replace,
    ReplaceLines,
    CopyFile,
    MoveFile,
    CreateDirectory,
}

internal sealed record AgentCommandArgumentDescriptor(
    string Name,
    string Description,
    string Flag,
    string? Pattern,
    IReadOnlyList<string>? AllowedValues,
    int? MaxLength
);

internal sealed record AgentCommandDescriptor(
    string Name,
    string Description,
    string Command,
    IReadOnlyList<AgentCommandArgumentDescriptor> Arguments
);

internal static class AgentSkillRuntime
{
    internal static void RegisterToolEffects(ToolEffectRegistry registry)
    {
        registry.Add(AgentSkillsProvider.LoadSkillToolName, ToolEffect.Read);
        registry.Add(AgentSkillsProvider.ReadSkillResourceToolName, ToolEffect.Read);
        registry.Add(AgentSkillsProvider.RunSkillScriptToolName, ToolEffect.ProcessExecution);
    }

    internal static AgentSkillsSource CreateSource(IReadOnlyList<AgentSkillDescriptor> skills)
    {
        var allowedDirectories = skills
            .Select(skill => skill.DirectoryPath)
            .ToHashSet(StringComparer.Ordinal);
        var source = new AgentFileSkillsSource(
            skills.Select(skill => skill.DirectoryPath),
            scriptRunner: null,
            new AgentFileSkillsSourceOptions { ScriptFilter = _ => false }
        );
        return new FilteringAgentSkillsSource(
            source,
            (candidate, _) =>
                candidate is AgentFileSkill fileSkill
                && allowedDirectories.Contains(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(fileSkill.Path))
                )
        );
    }

    internal static AgentSkillsProvider CreateProvider(
        IReadOnlyList<AgentSkillDescriptor> skills
    ) =>
        new(
            CreateSource(skills),
            new AgentSkillsProviderOptions
            {
                DisableLoadSkillApproval = true,
                DisableReadSkillResourceApproval = true,
                DisableRunSkillScriptApproval = false,
            },
            ownsSource: true
        );
}

internal enum ToolEffect
{
    Read,
    WorkspaceMutation,
    ProcessExecution,
    LifecycleTransition,
}

internal enum ToolEvidence
{
    None,
    RepositoryInspection,
}

internal sealed record ToolSemantics(
    ToolEffect Effect,
    ToolEvidence Evidence = ToolEvidence.None,
    Func<object?, ToolResultEvidenceDescriptor?>? ResultEvidence = null
);

internal sealed record ToolObservationDescriptor(string Name, ToolSemantics? Semantics);

internal enum ToolInvocationStatus
{
    Completed,
    Failed,
    Blocked,
    Faulted,
}

internal abstract record ToolResultEvidenceDescriptor
{
    internal sealed record Process(
        int ExitCode,
        string Stdout,
        string Stderr,
        TimeSpan Duration,
        bool TimedOut,
        bool Truncated
    ) : ToolResultEvidenceDescriptor;
}

internal sealed record ToolInvocationObservationDescriptor(
    string Name,
    ToolSemantics? Semantics,
    JsonElement Arguments,
    ToolInvocationStatus Status,
    ToolResultEvidenceDescriptor? Result
);

internal sealed class ToolEffectRegistry
{
    private readonly Dictionary<string, ToolSemantics> _semantics = new(StringComparer.Ordinal);

    internal void Add(
        string name,
        ToolEffect effect,
        ToolEvidence evidence = ToolEvidence.None,
        Func<object?, ToolResultEvidenceDescriptor?>? resultEvidence = null
    )
    {
        if (!_semantics.TryAdd(name, new ToolSemantics(effect, evidence, resultEvidence)))
        {
            throw new InvalidOperationException(
                $"Tool '{name}' has more than one authority classification."
            );
        }
    }

    internal bool TryGet(string name, out ToolSemantics semantics) =>
        _semantics.TryGetValue(name, out semantics!);
}

internal delegate AIAgent AgentImplementationFactory(AgentImplementationContext context);

internal static class GenericAgentInstructions
{
    internal const string Value = """
        You are an autonomous coding agent operating in Tandem, a multi-agent software-delivery workflow.

        Tandem assigns you one engineering role for this invocation and provides the repository, current
        lifecycle state, tools, and permitted actions. Independently complete that role's responsibility,
        then return the required capability call or structured result.

        The authored objective defines what is required. Mechanically supplied state governs lifecycle
        facts such as the current work item, verification status, and granted authority. The repository
        governs implementation and behavior facts. Verification records which configured commands passed.

        The ledger is a journal of previous agents' claims and actions, not a record of truth. Its entries
        may be incomplete, mistaken, stale, or confidently wrong. Use it only to understand prior activity
        or recover continuity. Establish every material repository conclusion yourself from the current
        repository. Ledger acceptance authenticates an event, not the truth of its contents.

        A capability transition occurs only when Tandem reports acceptance. Use only the capabilities
        provided for this invocation. Use read_ledger and search_ledger when lifecycle history is materially
        relevant.
        """;
}
