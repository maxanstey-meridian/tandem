using Microsoft.Agents.AI;

#pragma warning disable MAAI001

namespace Tandem.Infrastructure;

public sealed class GitExcludedFileStore : AgentFileStore
{
    private readonly AgentFileStore _inner;

    public GitExcludedFileStore(AgentFileStore inner)
    {
        _inner = inner;
    }

    public override Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken
    )
    {
        RejectGitPath(path);
        return _inner.WriteAsync(path, content, cancellationToken);
    }

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return _inner.ReadAsync(path, cancellationToken);
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return _inner.DeleteAsync(path, cancellationToken);
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken
    )
    {
        var entries = await _inner.ListChildrenAsync(directory, cancellationToken);
        return entries.Where(e => !IsGitSegment(e.Name)).ToList();
    }

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return _inner.FileExistsAsync(path, cancellationToken);
    }

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken
    )
    {
        var results = await _inner.SearchAsync(
            directory,
            regexPattern,
            globPattern,
            recursive,
            cancellationToken
        );
        return results.Where(r => !ContainsGitSegment(r.FileName)).ToList();
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return _inner.CreateDirectoryAsync(path, cancellationToken);
    }

    private static void RejectGitPath(string path)
    {
        if (ContainsGitSegment(path))
        {
            throw new UnauthorizedAccessException($"Access to '.git' paths is denied: {path}");
        }
    }

    private static bool ContainsGitSegment(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s == ".git");
    }

    private static bool IsGitSegment(string name) => name == ".git";
}

#pragma warning restore MAAI001
