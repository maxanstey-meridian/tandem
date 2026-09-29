using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class HarnessTools
{
    private sealed record FileTool(
        WorkspaceToolKind Kind,
        string Name,
        ToolEffect Effect,
        ToolEvidence Evidence,
        Func<string, AIFunction>? Create
    );

    // MAF's FileAccessProvider supplies the tools without a Create function.
    private static readonly FileTool[] _fileTools =
    [
        new(
            WorkspaceToolKind.ReadFile,
            FileAccessProvider.ReadFileToolName,
            ToolEffect.Read,
            ToolEvidence.RepositoryInspection,
            WorkspaceFileReadTools.Create
        ),
        new(
            WorkspaceToolKind.ListFiles,
            FileAccessProvider.LsToolName,
            ToolEffect.Read,
            ToolEvidence.RepositoryInspection,
            WorkspaceListTools.Create
        ),
        new(
            WorkspaceToolKind.Grep,
            FileAccessProvider.GrepToolName,
            ToolEffect.Read,
            ToolEvidence.RepositoryInspection,
            WorkspaceGrepTools.Create
        ),
        new(
            WorkspaceToolKind.WriteFile,
            FileAccessProvider.WriteToolName,
            ToolEffect.WorkspaceMutation,
            ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.DeleteFile,
            FileAccessProvider.DeleteFileToolName,
            ToolEffect.WorkspaceMutation,
            ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.Replace,
            FileAccessProvider.ReplaceToolName,
            ToolEffect.WorkspaceMutation,
            ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.ReplaceLines,
            FileAccessProvider.ReplaceLinesToolName,
            ToolEffect.WorkspaceMutation,
            ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.CopyFile,
            WorkspaceFileMutationTools.CopyToolName,
            ToolEffect.WorkspaceMutation,
            ToolEvidence.None,
            WorkspaceFileMutationTools.CreateCopyTool
        ),
        new(
            WorkspaceToolKind.MoveFile,
            WorkspaceFileMutationTools.MoveToolName,
            ToolEffect.WorkspaceMutation,
            ToolEvidence.None,
            WorkspaceFileMutationTools.CreateMoveTool
        ),
        new(
            WorkspaceToolKind.CreateDirectory,
            WorkspaceFileMutationTools.CreateDirectoryToolName,
            ToolEffect.WorkspaceMutation,
            ToolEvidence.None,
            WorkspaceFileMutationTools.CreateDirectoryTool
        ),
    ];

    internal static bool IsMutation(WorkspaceToolKind kind) =>
        _fileTools.Single(tool => tool.Kind == kind).Effect == ToolEffect.WorkspaceMutation;

    // Returns the selected tools that MAF's FileAccessProvider must expose.
    internal static IReadOnlySet<string> AddFileTools(
        ChatOptions options,
        ToolEffectRegistry effects,
        ResolvedAgentWorkspace workspace
    )
    {
        var mafToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in _fileTools.Where(tool => workspace.FileTools.Contains(tool.Kind)))
        {
            if (tool.Create is null)
            {
                effects.Add(tool.Name, tool.Effect, tool.Evidence);
                mafToolNames.Add(tool.Name);
            }
            else
            {
                Add(options, effects, tool.Create(workspace.Path), tool.Effect, tool.Evidence);
            }
        }
        return mafToolNames;
    }

    internal static void AddBuiltIn(ChatOptions options, ToolEffectRegistry effects, AITool tool)
    {
        var builtIn = BuiltInAgentTools.GroupTool(tool.Name);
        Add(options, effects, tool, builtIn.Effect, builtIn.Evidence);
    }

    internal static void Add(
        ChatOptions options,
        ToolEffectRegistry effects,
        AITool tool,
        ToolEffect effect,
        ToolEvidence evidence = ToolEvidence.None,
        Func<object?, ToolResultEvidence?>? resultEvidence = null
    )
    {
        var tools = options.Tools ?? [];
        if (tools.Any(existing => existing.Name == tool.Name))
        {
            throw new InvalidOperationException($"Agent already exposes tool '{tool.Name}'.");
        }
        options.Tools = [.. tools, tool];
        effects.Add(tool.Name, effect, evidence, resultEvidence);
    }
}

internal static class RegisteredWorkspaceTools
{
    internal static void Add(
        ChatOptions options,
        ResolvedAgentWorkspace workspace,
        ToolEffectRegistry effects
    )
    {
        foreach (var registration in workspace.RegisteredTools ?? [])
        {
            var tool = registration.Create(workspace.Path);
            if (!string.Equals(tool.Name, registration.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Registered workspace tool '{registration.Name}' created tool '{tool.Name}'."
                );
            }
            HarnessTools.Add(options, effects, tool, registration.Effect, registration.Evidence);
        }
    }
}

#pragma warning restore MAAI001
