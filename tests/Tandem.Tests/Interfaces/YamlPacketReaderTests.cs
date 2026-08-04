using FluentAssertions;
using Tandem.Interfaces;

namespace Tandem.Tests.Interfaces;

public sealed class YamlPacketReaderTests
{
    private const string CompletePacket = """
        ---
        title: "Add a greeting"
        repository: "{repo}"
        base: "main"
        outcomes:
          - id: "greeting"
            description: "Create greeting.txt containing Hello from Tandem."
        verification:
          - "test -f greeting.txt"
        constraints:
          - "Do not change existing files."
        ---

        Inspect the repository before making the requested change.
        """;

    [Fact]
    public void ParsesCompletePacket()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Dir, "example-repository"));
        var repo = Path.Combine(temp.Dir, "example-repository");
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, CompletePacket.Replace("{repo}", repo));

        var reader = new YamlPacketReader();
        var packet = reader.Read(packetPath);

        packet.Title.Should().Be("Add a greeting");
        packet.Repository.Should().Be(repo);
        packet.Base.Should().Be("main");
        packet.Outcomes.Should().HaveCount(1);
        packet.Outcomes[0].Id.Should().Be("greeting");
        packet
            .Outcomes[0]
            .Description.Should()
            .Be("Create greeting.txt containing Hello from Tandem.");
        packet.Verification.Should().ContainSingle().Which.Should().Be("test -f greeting.txt");
        packet
            .Constraints.Should()
            .ContainSingle()
            .Which.Should()
            .Be("Do not change existing files.");
        packet
            .ImplementationContext.Should()
            .Be("Inspect the repository before making the requested change.");
    }

    [Fact]
    public void EmptyVerificationAndConstraintsDefaultToEmpty()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Dir, "example-repository"));
        var repo = Path.Combine(temp.Dir, "example-repository");
        var minimal = $$"""
            ---
            title: "t"
            repository: "{{repo}}"
            base: "main"
            outcomes:
              - id: "a"
                description: "d"
            ---

            body
            """;
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, minimal);

        var packet = new YamlPacketReader().Read(packetPath);

        packet.Verification.Should().BeEmpty();
        packet.Constraints.Should().BeEmpty();
        packet.ImplementationContext.Should().Be("body");
    }

    [Fact]
    public void EmptyBodyIsAccepted()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Dir, "example-repository"));
        var repo = Path.Combine(temp.Dir, "example-repository");
        var minimal = $$"""
            ---
            title: "t"
            repository: "{{repo}}"
            base: "main"
            outcomes:
              - id: "a"
                description: "d"
            ---
            """;
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, minimal);

        var packet = new YamlPacketReader().Read(packetPath);

        packet.ImplementationContext.Should().BeEmpty();
    }

    [Fact]
    public void MissingFrontmatterStartFails()
    {
        using var temp = new TempDir();
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, "no frontmatter here");

        var act = () => new YamlPacketReader().Read(packetPath);

        act.Should().Throw<PacketException>().WithMessage("*frontmatter*");
    }

    [Fact]
    public void MissingFrontmatterEndFails()
    {
        using var temp = new TempDir();
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, "---\ntitle: t\n");

        var act = () => new YamlPacketReader().Read(packetPath);

        act.Should().Throw<PacketException>().WithMessage("*frontmatter*");
    }

    [Fact]
    public void MissingTitleFails()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Dir, "example-repository"));
        var repo = Path.Combine(temp.Dir, "example-repository");
        var bad = $$"""
            ---
            repository: "{{repo}}"
            base: "main"
            outcomes:
              - id: "a"
                description: "d"
            ---
            """;
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, bad);

        var act = () => new YamlPacketReader().Read(packetPath);

        act.Should().Throw<PacketException>().WithMessage("*title*");
    }

    [Fact]
    public void RelativeRepositoryResolvesAgainstPacketDirectory()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Dir, "sub", "repo"));
        var dir = Path.Combine(temp.Dir, "sub");
        var packetPath = Path.Combine(dir, "packet.md");
        File.WriteAllText(
            packetPath,
            """
            ---
            title: "t"
            repository: "repo"
            base: "main"
            outcomes:
              - id: "a"
                description: "d"
            ---
            """
        );

        var reader = new YamlPacketReader();
        var packet = reader.Read(packetPath);

        packet.Repository.Should().Be(Path.Combine(dir, "repo"));
    }

    [Fact]
    public void NonexistentRepositoryFails()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Dir, "does-not-exist");
        var bad = $$"""
            ---
            title: "t"
            repository: "{{repo}}"
            base: "main"
            outcomes:
              - id: "a"
                description: "d"
            ---
            """;
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, bad);

        var act = () => new YamlPacketReader().Read(packetPath);

        act.Should().Throw<PacketException>().WithMessage("*exist*");
    }

    [Fact]
    public void NoOutcomesFails()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Dir, "example-repository"));
        var repo = Path.Combine(temp.Dir, "example-repository");
        var bad = $$"""
            ---
            title: "t"
            repository: "{{repo}}"
            base: "main"
            outcomes: []
            ---
            """;
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, bad);

        var act = () => new YamlPacketReader().Read(packetPath);

        act.Should().Throw<PacketException>().WithMessage("*outcome*");
    }

    [Fact]
    public void DuplicateOutcomeIdsFail()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Dir, "example-repository"));
        var repo = Path.Combine(temp.Dir, "example-repository");
        var bad = $$"""
            ---
            title: "t"
            repository: "{{repo}}"
            base: "main"
            outcomes:
              - id: "a"
                description: "d1"
              - id: "a"
                description: "d2"
            ---
            """;
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, bad);

        var act = () => new YamlPacketReader().Read(packetPath);

        act.Should().Throw<PacketException>().WithMessage("*unique*");
    }

    [Fact]
    public void EmptyOutcomeIdFails()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Dir, "example-repository"));
        var repo = Path.Combine(temp.Dir, "example-repository");
        var bad = $$"""
            ---
            title: "t"
            repository: "{{repo}}"
            base: "main"
            outcomes:
              - id: ""
                description: "d"
            ---
            """;
        var packetPath = Path.Combine(temp.Dir, "packet.md");
        File.WriteAllText(packetPath, bad);

        var act = () => new YamlPacketReader().Read(packetPath);

        act.Should().Throw<PacketException>().WithMessage("*outcome id*");
    }

    [Fact]
    public void MissingFileFails()
    {
        var act = () => new YamlPacketReader().Read("/nonexistent/packet.md");

        act.Should().Throw<PacketException>().WithMessage("*not found*");
    }

    private sealed class TempDir : IDisposable
    {
        public string Dir { get; }

        public TempDir()
        {
            Dir = Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-test-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Dir);
        }

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
