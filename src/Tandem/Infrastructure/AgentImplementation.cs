using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Tandem.Infrastructure;

internal sealed record AgentImplementationContext(
    string Id,
    IChatClient ChatClient,
    ChatOptions ChatOptions,
    ResolvedAgentWorkspace? Workspace,
    ToolEffectRegistry ToolEffects,
    IReadOnlyList<AgentSkillDescriptor> Skills
);

internal sealed record ResolvedAgentWorkspace(
    string Path,
    IReadOnlySet<WorkspaceToolKind> FileTools,
    bool IncludeGitReadOnly,
    bool IncludeShell,
    IReadOnlyList<AgentCommandDescriptor> Commands
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
}

internal sealed record AgentCommandDescriptor(string Name, string Description, string Command);

internal static class AgentSkillRuntime
{
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
    internal const string Value =
        "You are one bounded node in a Tandem pipeline. Follow the authored instructions, "
        + "use only the capabilities provided for this invocation, produce the requested result, "
        + "and return control to Tandem. A capability transition occurs only when Tandem reports acceptance.";
}
