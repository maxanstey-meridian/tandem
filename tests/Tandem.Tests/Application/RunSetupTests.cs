using FluentAssertions;
using Tandem.Application;

namespace Tandem.Tests.Application;

public sealed class RunSetupTests
{
    [Fact]
    public void CreatesRunIdAndWorkspaceUnderTandemHome()
    {
        using var temp = new TempDir();
        var setup = new RunSetup();
        var paths = setup.Create(temp.Dir);

        paths.RunId.Should().NotBeEmpty();
        AssertUuidV7(paths.RunId);
        paths.RunDirectory.Should().StartWith(temp.Dir);
        paths.WorkspacePath.Should().EndWith("workspace");
        paths.WorkspacePath.Should().StartWith(paths.RunDirectory);
        Directory.Exists(paths.RunDirectory).Should().BeTrue();
    }

    [Fact]
    public void RunIdIsUuidV7()
    {
        using var temp = new TempDir();
        var setup = new RunSetup();
        AssertUuidV7(setup.Create(temp.Dir).RunId);
    }

    private static void AssertUuidV7(Guid runId)
    {
        // .NET Guid.ToByteArray returns the first 8 bytes in mixed-endian order (Data1/2/3 are
        // little-endian), so the RFC 9562 version nibble lands in the high nibble of byte 7.
        var bytes = runId.ToByteArray();
        (bytes[7] >> 4).Should().Be(0x7);
    }

    private sealed class TempDir : IDisposable
    {
        public string Dir { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-setup-" + Guid.NewGuid().ToString("N")
            );

        public TempDir() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, true);
            }
            catch { }
        }
    }
}
