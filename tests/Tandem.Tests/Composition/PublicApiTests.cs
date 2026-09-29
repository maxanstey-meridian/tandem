using System.ComponentModel;
using System.Reflection;
using FluentAssertions;

namespace Tandem.Tests.Composition;

/// <summary>
/// Each package's public surface is PublicAPI.Shipped.txt plus PublicAPI.Unshipped.txt, kept
/// exact by Microsoft.CodeAnalysis.PublicApiAnalyzers at build time (RS0016/RS0017).
/// </summary>
public sealed class PublicApiTests
{
    private static readonly string _src = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src")
    );

    private static readonly string[] _neverPublic = ["Microsoft.Agents", "Tandem.Infrastructure"];

    // Core authoring packages also keep provider, terminal and parsing libraries private.
    private static readonly string[] _neverPublicInAuthoring =
    [
        .. _neverPublic,
        "ModelContextProtocol",
        "OpenAI.",
        "Microsoft.Extensions.AI.OpenAI",
        "Spectre.Console",
        "YamlDotNet",
        "System.CommandLine",
    ];

    [Theory]
    [InlineData("Tandem", true)]
    [InlineData("Tandem.Advanced", true)]
    [InlineData("Tandem.Packets", true)]
    [InlineData("Tandem.Ledger", false)]
    [InlineData("Tandem.Terminal", false)]
    [InlineData("Tandem.OpenAICompatible", false)]
    [InlineData("Tandem.Generators", false)]
    public void PublicApi_ExposesNoMafTypes(string project, bool authoring)
    {
        var forbidden = authoring ? _neverPublicInAuthoring : _neverPublic;
        var leaks = new[] { "PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt" }
            .SelectMany(file => File.ReadLines(Path.Combine(_src, project, file)))
            .Where(line => forbidden.Any(name => line.Contains(name, StringComparison.Ordinal)));

        leaks.Should().BeEmpty();
    }

    [Fact]
    public void GeneratorAbi_IsHiddenFromOrdinaryDiscovery()
    {
        Type[] abi =
        [
            typeof(GeneratedOutcomeStepDescriptor<>),
            typeof(GeneratedPassThroughStepDescriptor<>),
            typeof(GeneratedStateStepDescriptor<>),
            typeof(GeneratedStepCompletion),
            typeof(IGeneratedPipelineStep<,>),
            typeof(IPipelineStep<>),
            typeof(IStandardOutcomePipelineStep<>),
            typeof(PipelineNodeDescriptor),
        ];

        abi.Select(type => type.GetCustomAttribute<EditorBrowsableAttribute>()?.State)
            .Should()
            .AllBeEquivalentTo(EditorBrowsableState.Never);
    }
}
