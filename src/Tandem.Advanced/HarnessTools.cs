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
        Infrastructure.ToolEffect Effect,
        Infrastructure.ToolEvidence Evidence,
        Func<string, AIFunction>? Create
    );

    // MAF's FileAccessProvider supplies the tools without a Create function.
    private static readonly FileTool[] _fileTools =
    [
        new(
            WorkspaceToolKind.ReadFile,
            FileAccessProvider.ReadFileToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection,
            WorkspaceFileReadTools.Create
        ),
        new(
            WorkspaceToolKind.ListFiles,
            FileAccessProvider.LsToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection,
            WorkspaceListTools.Create
        ),
        new(
            WorkspaceToolKind.Grep,
            FileAccessProvider.GrepToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection,
            WorkspaceGrepTools.Create
        ),
        new(
            WorkspaceToolKind.WriteFile,
            FileAccessProvider.WriteToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.DeleteFile,
            FileAccessProvider.DeleteFileToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.Replace,
            FileAccessProvider.ReplaceToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.ReplaceLines,
            FileAccessProvider.ReplaceLinesToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.CopyFile,
            WorkspaceFileMutationTools.CopyToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            WorkspaceFileMutationTools.CreateCopyTool
        ),
        new(
            WorkspaceToolKind.MoveFile,
            WorkspaceFileMutationTools.MoveToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            WorkspaceFileMutationTools.CreateMoveTool
        ),
        new(
            WorkspaceToolKind.CreateDirectory,
            WorkspaceFileMutationTools.CreateDirectoryToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            WorkspaceFileMutationTools.CreateDirectoryTool
        ),
    ];

    internal static bool IsMutation(WorkspaceToolKind kind) =>
        _fileTools.Single(tool => tool.Kind == kind).Effect
        == Infrastructure.ToolEffect.WorkspaceMutation;

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

    internal static void Add(
        ChatOptions options,
        ToolEffectRegistry effects,
        AITool tool,
        Infrastructure.ToolEffect effect,
        Infrastructure.ToolEvidence evidence = Infrastructure.ToolEvidence.None,
        Func<object?, ToolResultEvidenceDescriptor?>? resultEvidence = null
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
