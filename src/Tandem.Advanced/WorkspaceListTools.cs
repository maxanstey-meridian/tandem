using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001
namespace Tandem.Advanced;

internal enum ListEntryType
{
    [JsonStringEnumMemberName("file")]
    File,

    [JsonStringEnumMemberName("directory")]
    Directory,

    [JsonStringEnumMemberName("link")]
    Link,
}

internal sealed record ListEntry(
    [property: JsonPropertyName("name")] string Name,
    [property:
        JsonPropertyName("type"),
        JsonConverter(typeof(JsonStringEnumConverter<ListEntryType>))
    ]
        ListEntryType Type
);

internal sealed record ListPage(
    [property: JsonPropertyName("entries")] IReadOnlyList<ListEntry> Entries,
    [property:
        JsonPropertyName("nextOffset"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)
    ]
        int? NextOffset
);

internal static class WorkspaceListTools
{
    internal static AIFunction Create(string workspace) =>
        AIFunctionFactory.Create(
            (
                [Description("Repository-relative directory.")] string directory = ".",
                [Description("Maximum entries, 1 to 500.")] int limit = 200,
                [Description("Zero-based entry offset; continues a previous page.")] int offset = 0
            ) => List(workspace, directory, limit, offset),
            FileAccessProvider.LsToolName,
            "List workspace directory entries, including normally search-excluded directories. Continue with the returned nextOffset; results are recomputed, so restart after edits. Git metadata is hidden; symbolic links are listed but cannot be traversed."
        );

    internal static ListPage List(
        string workspace,
        string directory = ".",
        int limit = 200,
        int offset = 0
    )
    {
        RecordPage.Validate(offset, limit);
        var path = WorkspacePathAuthority.Resolve(workspace, directory, "list");
        var entries = Directory
            .EnumerateFileSystemEntries(path)
            .Select(p => (Name: Path.GetFileName(p), Path: p))
            .Where(e => !string.Equals(e.Name, ".git", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit + 1)
            .ToArray();
        var page = new List<ListEntry>();
        foreach (var entry in entries.Take(limit))
        {
            try
            {
                var attributes = File.GetAttributes(entry.Path);
                page.Add(
                    new ListEntry(
                        entry.Name,
                        (attributes & FileAttributes.ReparsePoint) != 0 ? ListEntryType.Link
                            : (attributes & FileAttributes.Directory) != 0 ? ListEntryType.Directory
                            : ListEntryType.File
                    )
                );
            }
            catch (Exception error)
                when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return new ListPage(page, entries.Length > limit ? offset + limit : null);
    }
}
