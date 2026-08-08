namespace Tandem.Delivery;

internal static class RepositoryGrounding
{
    internal static bool IsInspectionTool(string name) =>
        name is "read" or "grep" or "glob"
        || name.StartsWith("file_access_read", StringComparison.Ordinal)
        || name.StartsWith("file_access_search", StringComparison.Ordinal)
        || name.StartsWith("file_access_list", StringComparison.Ordinal)
        || name.StartsWith("gitnexus_", StringComparison.Ordinal)
        || name.Contains("ast_grep", StringComparison.Ordinal);
}
