namespace Tandem.Tests;

internal sealed class TempDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tandem-");

    public string Path => _directory.FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose() => _directory.Delete(recursive: true);
}
