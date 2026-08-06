using System.Text;
using Microsoft.Agents.AI;

#pragma warning disable MAAI001

namespace Tandem.Infrastructure;

public sealed class BomlessFileSystemAgentFileStore(string rootPath) : AgentFileStore
{
    private static readonly UTF8Encoding _utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false
    );

    private readonly string _rootPath = Path.GetFullPath(rootPath);
    private readonly FileSystemAgentFileStore _inner = new(rootPath);

    public override async Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken
    )
    {
        var normalized = content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;

        // Let the SDK perform its traversal and symlink checks first.
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

#pragma warning restore MAAI001
