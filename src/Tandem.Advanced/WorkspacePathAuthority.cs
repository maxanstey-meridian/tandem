using Tandem.Infrastructure;

namespace Tandem.Advanced;

internal static class WorkspacePathAuthority
{
    internal static string Resolve(string workspacePath, string path, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            throw new WorkspacePathException("File paths must be relative to the workspace.");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var fullPath = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, fullPath);
        if (
            relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new WorkspacePathException("File paths must remain within the workspace.");
        }
        if (
            relative
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
        )
        {
            throw new WorkspacePathException("Access to Git metadata is not allowed.");
        }
        var current = root;
        foreach (
            var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkspacePathException(
                        $"Workspace {operation} paths cannot contain symbolic links or reparse points."
                    );
                }
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }
        return fullPath;
    }
}
