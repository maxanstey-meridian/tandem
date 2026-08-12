namespace Tandem.Packets;

public sealed record PacketFile<T>(T Value, string Context, PacketSource Source);

public sealed record PacketSource(string? Name, string? FullPath, string? Directory)
{
    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        if (Directory is null)
        {
            throw new InvalidOperationException(
                "A relative path cannot be resolved without a filesystem packet source."
            );
        }

        return Path.GetFullPath(path, Directory);
    }
}

public sealed record PacketProblem(
    string Path,
    string Message,
    int? Line = null,
    int? Column = null
);

public sealed class PacketFileException : Exception
{
    public PacketFileException(
        string message,
        string? sourceName,
        IReadOnlyList<PacketProblem> problems,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        SourceName = sourceName;
        Problems = problems;
    }

    public string? SourceName { get; }

    public IReadOnlyList<PacketProblem> Problems { get; }
}
