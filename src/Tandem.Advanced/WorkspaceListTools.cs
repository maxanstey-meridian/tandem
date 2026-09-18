using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001
namespace Tandem.Advanced;

internal sealed record ListPage(
    [property: JsonPropertyName("entries")] IReadOnlyList<object> Entries,
    [property:
        JsonPropertyName("nextOffset"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)
    ]
        int? NextOffset
);

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
                    [Description("Zero-based entry offset; continues a previous page.")]
                        int offset = 0
                ) => List(workspace, directory, limit, offset),
                FileAccessProvider.LsToolName,
                "List workspace directory entries, including normally search-excluded directories. Continue with the returned nextOffset; results are recomputed, so restart after edits. Git metadata is hidden; symbolic links are listed but cannot be traversed."
            )
        );
    }

    internal static ListPage List(
        string workspace,
        string directory = ".",
        int limit = 200,
        int offset = 0
    )
    {
        if (limit is < 1 or > 500)
        {
            throw new ToolInputException("limit must be from 1 to 500.");
        }
        if (offset < 0)
        {
            throw new PaginationValidationException(
                nameof(offset),
                "Offset cannot be negative. Retry at offset 0.",
                new { retryOffset = 0, retryLimit = Math.Clamp(limit, 1, 500) }
            );
        }

        var path = WorkspacePathAuthority.Resolve(workspace, directory, "list");
        var entries = Directory
            .EnumerateFileSystemEntries(path)
            .Select(p => new { Name = Path.GetFileName(p), Path = p })
            .Where(e => !string.Equals(e.Name, ".git", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Skip(offset)
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
        return new ListPage(page, entries.Length > limit ? offset + limit : null);
    }
}
