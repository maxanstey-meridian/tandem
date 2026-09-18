namespace Tandem.Advanced;

// The single owner of the workspace search exclusion policy. WorkspaceGrepTools
// prunes traversal with it and GitExcludedFileStore filters search results with it.
internal static class WorkspaceSearchPolicy
{
    internal static readonly HashSet<string> ExcludedDirectories = new(
        [
            ".angular",
            ".build",
            ".bundle",
            ".cache",
            ".dart_tool",
            ".eggs",
            ".expo",
            ".git",
            ".gradle",
            ".hg",
            ".idea",
            ".mypy_cache",
            ".next",
            ".nox",
            ".nuxt",
            ".nx",
            ".nyc_output",
            ".output",
            ".parcel-cache",
            ".pytest_cache",
            ".pnpm-store",
            ".ruff_cache",
            ".sass-cache",
            ".serverless",
            ".stack-work",
            ".svelte-kit",
            ".svn",
            ".tox",
            ".turbo",
            ".terraform",
            ".terragrunt-cache",
            ".venv",
            ".vite",
            ".vs",
            ".yarn",
            "_build",
            "__pycache__",
            "artifacts",
            "bin",
            "bower_components",
            "Binaries",
            "build",
            "coverage",
            "Carthage",
            "CMakeFiles",
            "deps",
            "DerivedData",
            "DerivedDataCache",
            "dist",
            "env",
            "jspm_packages",
            "Intermediate",
            "Library",
            "node_modules",
            "obj",
            "out",
            "Pods",
            "Saved",
            "site-packages",
            "target",
            "TestResults",
            "tmp",
            "storybook-static",
            "venv",
            "vendor",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    internal static readonly HashSet<string> BinaryExtensions = new(
        [
            ".7z",
            ".a",
            ".apk",
            ".avi",
            ".bin",
            ".bmp",
            ".bz2",
            ".class",
            ".db",
            ".deb",
            ".dmg",
            ".dll",
            ".dylib",
            ".ear",
            ".exe",
            ".flac",
            ".gif",
            ".gem",
            ".gz",
            ".ico",
            ".ipa",
            ".iso",
            ".jar",
            ".jpeg",
            ".jpg",
            ".mov",
            ".mp3",
            ".mp4",
            ".o",
            ".otf",
            ".nupkg",
            ".pdf",
            ".pdb",
            ".png",
            ".pyc",
            ".pyo",
            ".rar",
            ".rpm",
            ".so",
            ".sqlite",
            ".sqlite3",
            ".snupkg",
            ".tar",
            ".tgz",
            ".ttf",
            ".war",
            ".wasm",
            ".wav",
            ".webm",
            ".webp",
            ".whl",
            ".woff",
            ".woff2",
            ".xz",
            ".zip",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    internal static bool IsExcludedDirectory(string name) =>
        ExcludedDirectories.Contains(name)
        || name.StartsWith("bazel-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase);

    // Search results are filtered by non-final path segments; the final segment is the file name.
    internal static bool HasExcludedDirectorySegment(string path)
    {
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 && segments[..^1].Any(IsExcludedDirectory);
    }

    internal static bool HasBinaryExtension(string path)
    {
        var name = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return name is not null && BinaryExtensions.Contains(Path.GetExtension(name));
    }
}
