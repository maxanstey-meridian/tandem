using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class WorkspaceFileReadTools
{
    internal static AIFunction Create(string workspacePath) =>
        AIFunctionFactory.Create(
            async (
                [System.ComponentModel.Description("Repository-relative text file path.")]
                    string path,
                [System.ComponentModel.Description(
                    "One-based first line to read; use line numbers from grep."
                )]
                    int startLine = 1,
                [System.ComponentModel.Description("Maximum lines to return, from 1 to 2000.")]
                    int lineCount = 200,
                [System.ComponentModel.Description(
                    "Zero-based character offset within startLine; continues a truncated line using the returned nextCharacterOffset."
                )]
                    int characterOffset = 0,
                CancellationToken cancellationToken = default
            ) =>
                await BoundedLinePageReader.ReadAsync(
                    WorkspacePathAuthority.Resolve(workspacePath, path, "read"),
                    startLine,
                    lineCount,
                    characterOffset,
                    cancellationToken
                ),
            FileAccessProvider.ReadFileToolName,
            "Read source lines using startLine from grep. Continue with the returned nextStartLine and nextCharacterOffset, including the remainder of oversized lines. Restart after edits."
        );
}

internal static class WorkspaceFileMutationTools
{
    internal const string CopyToolName = "file_access_copy";
    internal const string MoveToolName = "file_access_move";
    internal const string CreateDirectoryToolName = "file_access_create_directory";

    internal static AIFunction CreateCopyTool(string workspacePath) =>
        AIFunctionFactory.Create(
            (
                string sourceFileName,
                string destinationFileName,
                bool overwrite,
                CancellationToken cancellationToken
            ) =>
                Copy(
                    workspacePath,
                    sourceFileName,
                    destinationFileName,
                    overwrite,
                    cancellationToken
                ),
            CopyToolName,
            "Copy an existing file byte-for-byte within the configured workspace."
        );

    internal static AIFunction CreateMoveTool(string workspacePath) =>
        AIFunctionFactory.Create(
            (
                string sourceFileName,
                string destinationFileName,
                bool overwrite,
                CancellationToken cancellationToken
            ) =>
                Move(
                    workspacePath,
                    sourceFileName,
                    destinationFileName,
                    overwrite,
                    cancellationToken
                ),
            MoveToolName,
            "Move an existing file byte-for-byte within the configured workspace."
        );

    internal static AIFunction CreateDirectoryTool(string workspacePath) =>
        AIFunctionFactory.Create(
            (string directoryName, CancellationToken cancellationToken) =>
                CreateDirectory(workspacePath, directoryName, cancellationToken),
            CreateDirectoryToolName,
            "Create a directory and any missing parent directories within the configured workspace."
        );

    internal static string Copy(
        string workspacePath,
        string sourceFileName,
        string destinationFileName,
        bool overwrite,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(workspacePath, sourceFileName);
        var destination = Resolve(workspacePath, destinationFileName);
        Transfer(() => File.Copy(source, destination, overwrite), destination, overwrite);
        return $"Copied '{sourceFileName}' to '{destinationFileName}'.";
    }

    internal static string Move(
        string workspacePath,
        string sourceFileName,
        string destinationFileName,
        bool overwrite,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(workspacePath, sourceFileName);
        var destination = Resolve(workspacePath, destinationFileName);
        Transfer(() => File.Move(source, destination, overwrite), destination, overwrite);
        return $"Moved '{sourceFileName}' to '{destinationFileName}'.";
    }

    private static void Transfer(Action transfer, string destination, bool overwrite)
    {
        try
        {
            transfer();
        }
        catch (IOException exception)
            when (!overwrite && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new ToolInputException(
                $"Destination already exists: {Path.GetFileName(destination)}. Nothing was overwritten. Choose another destination or explicitly allow overwrite. {exception.Message}"
            );
        }
    }

    internal static string CreateDirectory(
        string workspacePath,
        string directoryName,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Resolve(workspacePath, directoryName));
        return $"Created directory '{directoryName}'.";
    }

    private static string Resolve(string workspacePath, string fileName) =>
        WorkspacePathAuthority.Resolve(workspacePath, fileName, "mutation");
}

// MAF's file tools reach only write, read, exists and delete: Tandem replaces its grep, ls and read tools.
internal sealed class WorkspaceFileStore(string workspacePath) : AgentFileStore
{
    private static readonly UTF8Encoding _utf8WithoutBom = new(false);

    public override async Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken
    )
    {
        var fullPath = WorkspacePathAuthority.Resolve(workspacePath, path, "write");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            content.StartsWith('\uFEFF') ? content[1..] : content,
            _utf8WithoutBom,
            cancellationToken
        );
    }

    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = WorkspacePathAuthority.Resolve(workspacePath, path, "read");
        return File.Exists(fullPath)
            ? await File.ReadAllTextAsync(fullPath, cancellationToken)
            : null;
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = WorkspacePathAuthority.Resolve(workspacePath, path, "mutation");
        if (!File.Exists(fullPath))
        {
            return Task.FromResult(false);
        }
        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(WorkspacePathAuthority.Resolve(workspacePath, path, "read")));

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException("Tandem's workspace ls tool replaces MAF's.");

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException("Tandem's workspace grep tool replaces MAF's.");

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException("MAF's file tools do not create directories.");
}

#pragma warning restore MAAI001
