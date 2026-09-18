using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001
namespace Tandem.Advanced;

internal static class WorkspaceListTools
{
    internal static void Add(ChatOptions options, string workspace)
    {
        options.Tools ??= [];
        options.Tools.Add(
            AIFunctionFactory.Create(
                (
                    [Description("Repository-relative directory.")] string directory = ".",
                    [Description("Maximum entries, 1 to 500.")] int limit = 200,
                    [Description(
                        "Copy nextCursor from the preceding page with unchanged arguments."
                    )]
                        string? cursor = null
                ) => List(workspace, directory, limit, cursor),
                FileAccessProvider.LsToolName,
                "List workspace directory entries, including normally search-excluded directories. Follow nextCursor until hasMore is false. Git metadata is hidden; symbolic links are listed but cannot be traversed."
            )
        );
    }

    internal static object List(
        string workspace,
        string directory = ".",
        int limit = 200,
        string? cursor = null
    )
    {
        if (limit is < 1 or > 500)
        {
            throw new ToolInputException("limit must be from 1 to 500.");
        }

        var path = WorkspacePathAuthority.Resolve(workspace, directory, "list");
        var scope = ToolCursor.Scope("ls", Path.GetFullPath(workspace), path, limit);
        var after = cursor is null ? null : ToolCursor.Decode<string>(cursor, scope);
        var entries = Directory
            .EnumerateFileSystemEntries(path)
            .Select(p => new { Name = Path.GetFileName(p), Path = p })
            .Where(e => !string.Equals(e.Name, ".git", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Where(e => after is null || StringComparer.Ordinal.Compare(e.Name, after) > 0)
            .Take(limit + 1)
            .ToArray();
        var page = entries
            .Take(limit)
            .Select(e =>
            {
                try
                {
                    var attributes = File.GetAttributes(e.Path);
                    return new
                    {
                        name = e.Name,
                        type = (attributes & FileAttributes.ReparsePoint) != 0 ? "link"
                        : (attributes & FileAttributes.Directory) != 0 ? "directory"
                        : "file",
                    };
                }
                catch (Exception error)
                    when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    return null;
                }
            })
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();
        return new
        {
            entries = page,
            returnedCount = page.Length,
            hasMore = entries.Length > limit,
            nextCursor = entries.Length > limit
                ? ToolCursor.Encode(scope, entries[limit - 1].Name)
                : null,
            pagination = "Live directory listing. Copy nextCursor with unchanged arguments; restart after edits.",
        };
    }
}
