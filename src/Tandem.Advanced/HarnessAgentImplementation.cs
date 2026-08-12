using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class HarnessAgentImplementation
{
    internal static AIAgent Create(AgentImplementationContext context, string harnessInstructions)
    {
        var workspace = context.Workspace;
        AgentFileStore? fileStore = workspace is null
            ? null
            : new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(workspace.Path));
        var providers =
            context.Skills.Count == 0
                ? new List<AIContextProvider>()
                : [AgentSkillRuntime.CreateProvider(context.Skills)];
        if (fileStore is not null)
        {
            var selectedFileToolNames = HarnessToolEffects.Register(
                context.ToolEffects,
                workspace!.FileTools
            );
            if (selectedFileToolNames.Count > 0)
            {
                providers.Add(
                    new FilteringAIContextProvider(
                        new FileAccessProvider(
                            fileStore,
                            new FileAccessProviderOptions
                            {
                                DisableWriteTools = !workspace.FileTools.Any(IsMutation),
                                DisableReadOnlyToolApproval = true,
                                DisableWriteToolApproval = true,
                            }
                        ),
                        selectedFileToolNames
                    )
                );
            }
            if (workspace.IncludeGitReadOnly)
            {
                var existingToolCount = context.ChatOptions.Tools?.Count ?? 0;
                ReadOnlyGitTools.Add(context.ChatOptions, workspace.Path, context.ToolEffects);
                WorkspaceShellTools.Add(context.ChatOptions, workspace, context.ToolEffects);
                var workspaceTools =
                    context.ChatOptions.Tools?.Skip(existingToolCount).ToArray() ?? [];
                if (workspaceTools.Length > 0)
                {
                    context.ChatOptions.Tools = context
                        .ChatOptions.Tools?.Take(existingToolCount)
                        .ToList();
                    providers.Add(new StaticToolsAIContextProvider(workspaceTools));
                }
            }
            else
            {
                var existingToolCount = context.ChatOptions.Tools?.Count ?? 0;
                WorkspaceShellTools.Add(context.ChatOptions, workspace, context.ToolEffects);
                var workspaceTools =
                    context.ChatOptions.Tools?.Skip(existingToolCount).ToArray() ?? [];
                if (workspaceTools.Length > 0)
                {
                    context.ChatOptions.Tools = context
                        .ChatOptions.Tools?.Take(existingToolCount)
                        .ToList();
                    providers.Add(new StaticToolsAIContextProvider(workspaceTools));
                }
            }
        }
        return new HarnessAgent(
            context.ChatClient,
            new HarnessAgentOptions
            {
                Id = context.Id,
                Name = context.Id,
                HarnessInstructions = harnessInstructions,
                ChatOptions = context.ChatOptions,
                DisableFileMemory = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableAgentSkillsProvider = true,
                AIContextProviders = providers.Count == 0 ? null : providers,
                DisableWebSearch = true,
                DisableToolAutoApproval = true,
                DisableOpenTelemetry = true,
                DisableCompaction = true,
                MaximumIterationsPerRequest = 999,
                FileAccessStore = null,
            }
        );
    }

    private static bool IsMutation(WorkspaceToolKind kind) =>
        kind
            is WorkspaceToolKind.WriteFile
                or WorkspaceToolKind.DeleteFile
                or WorkspaceToolKind.Replace
                or WorkspaceToolKind.ReplaceLines;
}

internal static class HarnessToolEffects
{
    internal static IReadOnlySet<string> Register(
        ToolEffectRegistry registry,
        IReadOnlySet<WorkspaceToolKind> selected
    )
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in selected)
        {
            var (name, effect, evidence) = kind switch
            {
                WorkspaceToolKind.ReadFile => (
                    FileAccessProvider.ReadFileToolName,
                    Infrastructure.ToolEffect.Read,
                    Infrastructure.ToolEvidence.RepositoryInspection
                ),
                WorkspaceToolKind.ListFiles => (
                    FileAccessProvider.LsToolName,
                    Infrastructure.ToolEffect.Read,
                    Infrastructure.ToolEvidence.RepositoryInspection
                ),
                WorkspaceToolKind.Grep => (
                    FileAccessProvider.GrepToolName,
                    Infrastructure.ToolEffect.Read,
                    Infrastructure.ToolEvidence.RepositoryInspection
                ),
                WorkspaceToolKind.WriteFile => (
                    FileAccessProvider.WriteToolName,
                    Infrastructure.ToolEffect.WorkspaceMutation,
                    Infrastructure.ToolEvidence.None
                ),
                WorkspaceToolKind.DeleteFile => (
                    FileAccessProvider.DeleteFileToolName,
                    Infrastructure.ToolEffect.WorkspaceMutation,
                    Infrastructure.ToolEvidence.None
                ),
                WorkspaceToolKind.Replace => (
                    FileAccessProvider.ReplaceToolName,
                    Infrastructure.ToolEffect.WorkspaceMutation,
                    Infrastructure.ToolEvidence.None
                ),
                WorkspaceToolKind.ReplaceLines => (
                    FileAccessProvider.ReplaceLinesToolName,
                    Infrastructure.ToolEffect.WorkspaceMutation,
                    Infrastructure.ToolEvidence.None
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            registry.Add(name, effect, evidence);
            names.Add(name);
        }
        return names;
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

internal static class WorkspaceShellTools
{
    internal static void Add(
        ChatOptions options,
        ResolvedAgentWorkspace workspace,
        ToolEffectRegistry effects,
        TimeSpan? timeout = null,
        int maxOutputBytes = 64 * 1024
    )
    {
        var tools = options.Tools?.ToList() ?? [];
        foreach (var command in workspace.Commands)
        {
            var authoredCommand = command.Command;
            tools.Add(
                AIFunctionFactory.Create(
                    async (CancellationToken cancellationToken) =>
                    {
                        await using var shell = CreateExecutor(
                            workspace.Path,
                            acknowledgeUnsafe: false,
                            timeout,
                            maxOutputBytes
                        );
                        return await shell.RunAsync(authoredCommand, cancellationToken);
                    },
                    command.Name,
                    command.Description
                )
            );
            effects.Add(
                command.Name,
                Infrastructure.ToolEffect.ProcessExecution,
                resultEvidence: ToProcessEvidence
            );
        }
        if (workspace.IncludeShell)
        {
            var tool = CreateExecutor(
                    workspace.Path,
                    acknowledgeUnsafe: true,
                    timeout,
                    maxOutputBytes
                )
                .AsAIFunction(
                    "run_shell",
                    "Run a model-authored command in the configured workspace without approval.",
                    requireApproval: false
                );
            tools.Add(tool);
            effects.Add(tool.Name, Infrastructure.ToolEffect.ProcessExecution);
        }
        options.Tools = tools;
    }

    private static ToolResultEvidenceDescriptor.Process? ToProcessEvidence(object? result)
    {
        if (result is ShellResult shellResult)
        {
            return new ToolResultEvidenceDescriptor.Process(
                shellResult.ExitCode,
                shellResult.Stdout,
                shellResult.Stderr,
                shellResult.Duration,
                shellResult.TimedOut,
                shellResult.Truncated
            );
        }
        if (result is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        try
        {
            var evidence = element.Deserialize<ShellResultEvidence>(JsonSerializerOptions.Web);
            return evidence is null
                ? null
                : new ToolResultEvidenceDescriptor.Process(
                    evidence.ExitCode,
                    evidence.Stdout ?? string.Empty,
                    evidence.Stderr ?? string.Empty,
                    evidence.Duration,
                    evidence.TimedOut,
                    evidence.Truncated
                );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ShellResultEvidence(
        int ExitCode,
        string? Stdout,
        string? Stderr,
        TimeSpan Duration,
        bool TimedOut,
        bool Truncated
    );

    private static LocalShellExecutor CreateExecutor(
        string workspacePath,
        bool acknowledgeUnsafe,
        TimeSpan? timeout,
        int maxOutputBytes
    ) =>
        new(
            new LocalShellExecutorOptions
            {
                Mode = ShellMode.Stateless,
                WorkingDirectory = workspacePath,
                ConfineWorkingDirectory = true,
                Timeout = timeout ?? TimeSpan.FromMinutes(10),
                MaxOutputBytes = maxOutputBytes,
                AcknowledgeUnsafe = acknowledgeUnsafe,
            }
        );
}

internal sealed class BomlessFileSystemAgentFileStore(string rootPath) : AgentFileStore
{
    private static readonly UTF8Encoding _utf8WithoutBom = new(false);
    private readonly string _rootPath = Path.GetFullPath(rootPath);
    private readonly FileSystemAgentFileStore _inner = new(rootPath);

    public override async Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken
    )
    {
        var normalized = content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
        await _inner.WriteAsync(path, normalized, cancellationToken);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, path));
        await File.WriteAllTextAsync(fullPath, normalized, _utf8WithoutBom, cancellationToken);
    }

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken) =>
        _inner.ReadAsync(path, cancellationToken);

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken) =>
        _inner.DeleteAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken
    ) => _inner.ListChildrenAsync(directory, cancellationToken);

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        _inner.FileExistsAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken
    ) => _inner.SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        _inner.CreateDirectoryAsync(path, cancellationToken);
}

internal sealed class GitExcludedFileStore(AgentFileStore inner) : AgentFileStore
{
    public override Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken
    )
    {
        RejectGitPath(path);
        var normalized = content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
        return inner.WriteAsync(path, normalized, cancellationToken);
    }

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return inner.ReadAsync(path, cancellationToken);
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return inner.DeleteAsync(path, cancellationToken);
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken
    ) =>
        (await inner.ListChildrenAsync(directory, cancellationToken))
            .Where(entry => !string.Equals(entry.Name, ".git", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return inner.FileExistsAsync(path, cancellationToken);
    }

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken
    ) =>
        (
            await inner.SearchAsync(
                directory,
                regexPattern,
                globPattern,
                recursive,
                cancellationToken
            )
        )
            .Where(result => !ContainsGitSegment(result.FileName))
            .ToList();

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return inner.CreateDirectoryAsync(path, cancellationToken);
    }

    private static void RejectGitPath(string path)
    {
        if (ContainsGitSegment(path))
        {
            throw new UnauthorizedAccessException($"Access to '.git' paths is denied: {path}");
        }
    }

    private static bool ContainsGitSegment(string path) =>
        path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));
}

#pragma warning restore MAAI001
