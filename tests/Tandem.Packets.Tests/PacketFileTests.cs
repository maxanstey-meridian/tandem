using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using Tandem.Packets;

namespace Tandem.Packets.Tests;

public sealed class PacketFileTests
{
    private static readonly string Fixtures = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "packet-fixtures")
    );

    [Fact]
    public void Parse_ConstructsImmutableNestedRecordsAndNormalizesContext()
    {
        var input = PacketFile.Read<Packet>(Path.Combine(Fixtures, "valid-nested.md"));

        input.Value.Title.Should().Be("Implement registration");
        input.Value.Outcomes.Should().ContainSingle().Which.Id.Should().Be("registration");
        input.Value.Constraints.Should().BeEmpty();
        input.Value.Mode.Should().Be(PacketMode.Strict);
        input.Value.Note.Should().BeNull();
        input.Context.Should().Be("Inspect authentication.\n\n---\nThis is Markdown.");
        input.Source.FullPath.Should().Be(Path.Combine(Fixtures, "valid-nested.md"));
        input
            .Source.ResolvePath(input.Value.Repository)
            .Should()
            .Be(Path.Combine(Fixtures, "my-app"));
    }

    [Fact]
    public void Parse_AcceptsBomAndCrLfAndUsesOptionalCollectionDefault()
    {
        var content =
            "\uFEFF---\r\ntitle: Test\r\nrepository: .\r\noutcomes: []\r\nverification: []\r\nmode: normal\r\n---\r\n body \r\n";
        var input = PacketFile.Parse<Packet>(content, "memory.packet");

        input.Context.Should().Be("body");
        input.Value.Constraints.Should().BeEmpty();
        input.Source.Name.Should().Be("memory.packet");
        input
            .Source.Invoking(source => source.ResolvePath("relative"))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Fact]
    public void Parse_TranslatesFluentValidationProblems()
    {
        var validator = new InlineValidator<Packet>();
        validator.RuleFor(packet => packet.Title).NotEqual("Test");
        validator.RuleFor(packet => packet.Repository).NotEqual(".");

        var action = () =>
            PacketFile.Parse(
                "---\ntitle: Test\nrepository: .\noutcomes: []\nverification: []\nmode: normal\n---",
                validator,
                "validation.packet"
            );

        var exception = action.Should().Throw<PacketFileException>().Which;
        exception.SourceName.Should().Be("validation.packet");
        exception
            .Problems.Select(problem => problem.Path)
            .Should()
            .Equal("$.title", "$.repository");
    }

    [Fact]
    public void Parse_ReportsThePathLocationAndExpectedTypeForShapeProblems()
    {
        var action = () =>
            PacketFile.Parse<Packet>(
                """
                ---
                title: Test
                repository: .
                outcomes: []
                verification: []
                constraints:
                  - valid
                  - invalid: object
                mode: normal
                ---
                """,
                "shape.packet"
            );

        var exception = action.Should().Throw<PacketFileException>().Which;
        exception.Message.Should().Be("Packet shape is invalid.");
        exception
            .Problems.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new PacketProblem("$.constraints[1]", "Value must be a string.", 8, 5));
    }

    [Fact]
    public async Task ReadAsync_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var action = async () =>
            await PacketFile.ReadAsync<Packet>(
                Path.Combine(Fixtures, "valid-nested.md"),
                cancellation.Token
            );
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void SharedFixtures_MatchPortableContract()
    {
        var manifest = JsonSerializer.Deserialize<Fixture[]>(
            File.ReadAllText(Path.Combine(Fixtures, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;

        foreach (var fixture in manifest)
        {
            var action = () => PacketFile.Read<Packet>(Path.Combine(Fixtures, fixture.File));
            if (fixture.Valid)
            {
                action.Should().NotThrow(fixture.File);
            }
            else
            {
                action
                    .Should()
                    .Throw<PacketFileException>(fixture.File)
                    .Which.Problems.Should()
                    .Contain(problem => problem.Path == fixture.Path);
            }
        }
    }

    [Fact]
    public void Parse_RejectsExcessiveSourceAndNesting()
    {
        var oversized = "---\ntitle: " + new string('x', PacketFile.MaximumSourceBytes) + "\n---";
        Action oversizedAction = () => PacketFile.Parse<Packet>(oversized);
        oversizedAction.Should().Throw<PacketFileException>();

        var nestedValue = "value";
        for (var depth = 0; depth < PacketFile.MaximumDepth + 2; depth++)
        {
            nestedValue =
                $"child:\n{string.Join('\n', nestedValue.Split('\n').Select(line => "  " + line))}";
        }
        var nested =
            "---\ntitle: Test\nrepository: .\noutcomes: []\nverification: []\nmode: normal\nvalue:\n"
            + string.Join('\n', nestedValue.Split('\n').Select(line => "  " + line))
            + "\n---";
        Action nestedAction = () => PacketFile.Parse<Packet>(nested);
        nestedAction
            .Should()
            .Throw<PacketFileException>()
            .Which.Problems.Should()
            .Contain(problem => problem.Message.Contains("depth", StringComparison.Ordinal));
    }

    public sealed record Packet(
        string Title,
        string Repository,
        IReadOnlyList<PacketOutcome> Outcomes,
        IReadOnlyList<string> Verification,
        PacketMode Mode,
        IReadOnlyList<string>? Constraints = null,
        string? Note = null
    )
    {
        public IReadOnlyList<string> Constraints { get; init; } = Constraints ?? [];
    }

    public sealed record PacketOutcome(string Id, string Description);

    public enum PacketMode
    {
        Normal,
        Strict,
    }

    private sealed record Fixture(string File, bool Valid, string? Path);
}
