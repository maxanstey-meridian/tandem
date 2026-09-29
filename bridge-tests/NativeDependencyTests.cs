using Xunit;

namespace Tandem.Bridge;

public sealed class NativeDependencyTests
{
    [Theory]
    [InlineData("osx-arm64", "libe_sqlite3.dylib")]
    [InlineData("linux-x64", "libe_sqlite3.so")]
    [InlineData("linux-musl-x64", "libe_sqlite3.so")]
    [InlineData("win-x64", "e_sqlite3.dll")]
    public void Native_library_file_name_follows_the_runtime_identifier(
        string runtimeIdentifier,
        string expected
    ) =>
        Assert.Equal(
            expected,
            NodePipelineBridge.NativeLibraryFileName("e_sqlite3", runtimeIdentifier)
        );
}
