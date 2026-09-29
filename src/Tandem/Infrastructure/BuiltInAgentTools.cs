using Microsoft.Agents.AI;

namespace Tandem.Infrastructure;

internal sealed record BuiltInTool(
    string Name,
    ToolEffect Effect,
    ToolEvidence Evidence = ToolEvidence.None
);

/// <summary>
/// Names and authority of the tools Tandem itself attaches to agents. Workspace file tools are
/// selected by name here; the Harness exposes them under MAF's own tool names and classifies
/// them from MAF's constants in Advanced.
/// </summary>
internal static class BuiltInAgentTools
{
    public static IReadOnlyDictionary<string, WorkspaceToolKind> FileSelections { get; } =
        new Dictionary<string, WorkspaceToolKind>(StringComparer.Ordinal)
        {
            ["read_file"] = WorkspaceToolKind.ReadFile,
            ["ls"] = WorkspaceToolKind.ListFiles,
            ["grep"] = WorkspaceToolKind.Grep,
            ["write_file"] = WorkspaceToolKind.WriteFile,
            ["delete_file"] = WorkspaceToolKind.DeleteFile,
            ["replace"] = WorkspaceToolKind.Replace,
            ["replace_lines"] = WorkspaceToolKind.ReplaceLines,
            ["copy_file"] = WorkspaceToolKind.CopyFile,
            ["move_file"] = WorkspaceToolKind.MoveFile,
            ["create_directory"] = WorkspaceToolKind.CreateDirectory,
        };

    public const string GitReadOnlyGroup = "git:ro";
    public const string ShellGroup = "shell";
    public const string WebSearchGroup = "web_search";
    public const string WebFetchGroup = "web_fetch";

    public static IReadOnlyDictionary<string, IReadOnlyList<BuiltInTool>> Groups { get; } =
        new Dictionary<string, IReadOnlyList<BuiltInTool>>(StringComparer.Ordinal)
        {
            [GitReadOnlyGroup] =
            [
                .. new[]
                {
                    "git_status",
                    "git_diff",
                    "git_log",
                    "git_show",
                    "git_blame",
                    "git_changed_files",
                    "git_compare",
                }.Select(name => new BuiltInTool(
                    name,
                    ToolEffect.Read,
                    ToolEvidence.RepositoryInspection
                )),
            ],
            [ShellGroup] = [new("run_shell", ToolEffect.ProcessExecution)],
            [WebSearchGroup] = [new("web_search", ToolEffect.Read)],
            [WebFetchGroup] = [new("web_fetch", ToolEffect.Read)],
        };

    private static readonly IReadOnlyDictionary<string, BuiltInTool> _groupTools = Groups
        .Values.SelectMany(tools => tools)
        .ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    /// <summary>The authority of a tool that a workspace tool group attaches.</summary>
    public static BuiltInTool GroupTool(string name) =>
        _groupTools.TryGetValue(name, out var tool)
            ? tool
            : throw new InvalidOperationException($"'{name}' is not a built-in group tool.");

    /// <summary>Whether a workspace may select <paramref name="name"/> as a built-in tool.</summary>
    public static bool IsSelectable(string name) =>
        FileSelections.ContainsKey(name) || Groups.ContainsKey(name);

    public const string ReadLedgerEntry = "read_ledger_entry";
    public const string ReadLedger = "read_ledger";
    public const string SearchLedger = "search_ledger";

    public static IReadOnlyList<BuiltInTool> Ledger { get; } =
    [
        new(ReadLedgerEntry, ToolEffect.Read),
        new(ReadLedger, ToolEffect.Read),
        new(SearchLedger, ToolEffect.Read),
    ];

    public static IReadOnlyList<BuiltInTool> Skills { get; } =
    [
        new(AgentSkillsProvider.LoadSkillToolName, ToolEffect.Read),
        new(AgentSkillsProvider.ReadSkillResourceToolName, ToolEffect.Read),
        new(AgentSkillsProvider.RunSkillScriptToolName, ToolEffect.ProcessExecution),
    ];

    public static IReadOnlySet<string> ReservedWorkspaceNames { get; } =
        new HashSet<string>(
            [
                .. FileSelections.Keys,
                .. Groups.Keys,
                .. Groups.Values.SelectMany(tools => tools.Select(tool => tool.Name)),
                .. Skills.Select(tool => tool.Name),
            ],
            StringComparer.Ordinal
        );

    public static void Register(ToolEffectRegistry registry, IEnumerable<BuiltInTool> tools)
    {
        foreach (var tool in tools)
        {
            registry.Add(tool.Name, tool.Effect, tool.Evidence);
        }
    }

    public static bool Contains(IEnumerable<BuiltInTool> tools, string name) =>
        tools.Any(tool => tool.Name == name);
}
