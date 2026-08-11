using System.Text;
using Microsoft.Agents.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class HarnessAgentImplementation
{
    internal static AIAgent Create(AgentImplementationContext context, string harnessInstructions)
    {
        AgentFileStore? fileStore = context.WorkspacePath is null
            ? null
            : new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(context.WorkspacePath));
        if (fileStore is not null)
        {
            HarnessToolEffects.Register(context.ToolEffects, context.ExposeWorkspaceMutationTools);
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
                AIContextProviders =
                    context.Skills.Count == 0
                        ? null
                        : [AgentSkillRuntime.CreateProvider(context.Skills)],
                DisableWebSearch = true,
                DisableToolAutoApproval = true,
                DisableOpenTelemetry = true,
                DisableCompaction = true,
                MaximumIterationsPerRequest = 999,
                FileAccessStore = fileStore,
                FileAccessProviderOptions = fileStore is null
                    ? null
                    : new FileAccessProviderOptions
                    {
                        DisableWriteTools = !context.ExposeWorkspaceMutationTools,
                        DisableReadOnlyToolApproval = true,
                        DisableWriteToolApproval = true,
                    },
            }
        );
    }
}

internal static class HarnessToolEffects
{
    internal static void Register(ToolEffectRegistry registry, bool includeMutations)
    {
        registry.Add(
            FileAccessProvider.ReadFileToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection
        );
        registry.Add(
            FileAccessProvider.LsToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection
        );
        registry.Add(
            FileAccessProvider.GrepToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection
        );
        if (!includeMutations)
        {
            return;
        }
        registry.Add(FileAccessProvider.WriteToolName, Infrastructure.ToolEffect.WorkspaceMutation);
        registry.Add(
            FileAccessProvider.DeleteFileToolName,
            Infrastructure.ToolEffect.WorkspaceMutation
        );
        registry.Add(
            FileAccessProvider.ReplaceToolName,
            Infrastructure.ToolEffect.WorkspaceMutation
        );
        registry.Add(
            FileAccessProvider.ReplaceLinesToolName,
            Infrastructure.ToolEffect.WorkspaceMutation
        );
    }
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
            .Where(entry => entry.Name != ".git")
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
            .Any(segment => segment == ".git");
}

#pragma warning restore MAAI001
