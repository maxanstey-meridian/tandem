using System.Xml.Linq;
using FluentAssertions;

namespace Tandem.Tests.Composition;

public sealed class ProjectBoundaryTests
{
    private static readonly string _root = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
    );

    [Fact]
    public void ProjectReferences_KeepDeliveryOutOfTandemAndInTheHost()
    {
        var tandem = ProjectReferences("src/Tandem/Tandem.csproj");
        var delivery = ProjectReferences("src/Tandem.Delivery/Tandem.Delivery.csproj");
        var tool = ProjectReferences("src/Tandem.Tool/Tandem.Tool.csproj");
        var debate = ProjectReferences("samples/Tandem.Sample.Debate/Tandem.Sample.Debate.csproj");

        tandem.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
        delivery.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        tool.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        tool.Should().Contain(reference => reference.EndsWith("Tandem.Delivery.csproj"));
        debate.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        debate.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
    }

    [Fact]
    public void Debate_IsAnUnprivilegedConsumerWithoutMafOrDeliveryVocabulary()
    {
        var project = File.ReadAllText(
            Path("samples/Tandem.Sample.Debate/Tandem.Sample.Debate.csproj")
        );
        project.Should().NotContain("Microsoft.Agents");
        project.Should().NotContain("Tandem.Delivery");
        project.Should().NotContain("Compile Include");
        project.Should().NotContain("InternalsVisibleTo");

        var source = Directory
            .EnumerateFiles(
                Path("samples/Tandem.Sample.Debate"),
                "*.cs",
                SearchOption.AllDirectories
            )
            .Where(file =>
                !file.Contains(
                    $"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"
                )
            )
            .Where(file =>
                !file.Contains(
                    $"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}"
                )
            )
            .SelectMany(File.ReadLines)
            .ToArray();
        source.Should().NotContain(line => line.Contains("using Microsoft.Agents"));
        source.Should().NotContain(line => line.Contains("Tandem.Delivery"));
        source.Should().NotContain(line => line.Contains("Tandem.Infrastructure.Composition"));
        source.Should().NotContain(line => line.Contains("System.Reflection"));
        source.Should().NotContain(line => line.Contains("InternalsVisibleTo"));
    }

    [Fact]
    public void TandemProduction_HasNoDebateSpecificTypesOrSwitches()
    {
        var source = Directory
            .EnumerateFiles(Path("src/Tandem"), "*.cs", SearchOption.AllDirectories)
            .Concat(
                Directory.EnumerateFiles(
                    Path("src/Tandem.Tool"),
                    "*.cs",
                    SearchOption.AllDirectories
                )
            )
            .SelectMany(File.ReadLines);
        source.Should().NotContain(line => line.Contains("Debate", StringComparison.Ordinal));
    }

    [Fact]
    public void Delivery_HasNoDirectMafReferenceOrNamespaceImport()
    {
        var project = File.ReadAllText(Path("src/Tandem.Delivery/Tandem.Delivery.csproj"));
        project.Should().NotContain("Microsoft.Agents");

        var source = Directory
            .EnumerateFiles(Path("src/Tandem.Delivery"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines);
        source.Should().NotContain(line => line.Contains("using Microsoft.Agents"));
    }

    [Fact]
    public void ConsumerProjects_ImportNoMafNamespaces()
    {
        SourceLines("src/Tandem.Delivery")
            .Concat(SourceLines("samples/Tandem.Sample.Debate"))
            .Should()
            .NotContain(line => line.Contains("Microsoft.Agents", StringComparison.Ordinal));
    }

    [Fact]
    public void Tandem_HasNoDeliveryAssumptionsOrParallelRouteModel()
    {
        var source = SourceLines("src/Tandem").ToArray();

        source.Should().NotContain(line => line.Contains("Tandem.Delivery"));
        source.Should().NotContain(line => line.Contains("DeliveryState"));
        source.Should().NotContain(line => line.Contains("DeliveryLifecycleActions"));
        source.Should().NotContain(line => line.Contains("RouteDefinition"));
        source.Should().NotContain(line => line.Contains("RouteRegistry"));
        source.Should().NotContain(line => line.Contains("RouteMap"));
    }

    [Fact]
    public void AuthoredSteps_UseDunetAndGeneratedSelectors()
    {
        foreach (var root in new[] { "src/Tandem.Delivery", "samples/Tandem.Sample.Debate" })
        {
            var source = string.Join('\n', SourceLines(root));
            source.Should().Contain("using Dunet;");
            source.Should().Contain("[Union");
            source.Should().Contain(".Result.");
            source.Should().NotContain("class ResultCase");
            source.Should().NotContain("record ResultCase");
            source.Should().NotContain("ExecutorBinding");
        }
    }

    [Fact]
    public void EveryAgentConstruction_SuppliesAnExplicitSessionPolicy()
    {
        var source = File.ReadAllText(Path("src/Tandem.Delivery/DeliveryStepsFactory.cs"));
        source.Should().Contain("sessionPolicy: ExecutorPolicies.ContinueWorkingSession");
        source.Should().Contain("sessionPolicy: PlannerPolicies.ContinueConsultation");
        source.Should().Contain("sessionPolicy: ReviewerPolicies.StartFreshForEachCandidate");

        var debate = string.Join('\n', SourceLines("samples/Tandem.Sample.Debate"));
        debate.Should().Contain("SessionPolicy:");
    }

    [Fact]
    public void PipelineAuthoring_RetainsNoRouteCollectionAndRegistersEdgesDirectly()
    {
        var source = File.ReadAllText(Path("src/Tandem/Authoring/PipelineStep.cs"));
        source.Should().Contain("_builder.AddEdge");
        source.Should().NotContain("_routes");
        source.Should().NotContain("RouteDefinition");
    }

    [Fact]
    public void Tool_PersistsCompositionMetadataBeforePublishingAnAttachableRunId()
    {
        var source = File.ReadAllText(Path("src/Tandem.Tool/Program.cs"));
        var persistence = source.IndexOf("RunProjection.Initial(", StringComparison.Ordinal);
        var publication = source.IndexOf(
            "Console.WriteLine($\"Run:       {runPaths.RunId}\")",
            StringComparison.Ordinal
        );

        persistence.Should().BeGreaterThan(-1);
        publication.Should().BeGreaterThan(persistence);
        source
            .Should()
            .Contain("projection.CompositionIdentity != DeliveryLifecycleActions.Identity");
    }

    private static IReadOnlyList<string> ProjectReferences(string relativePath) =>
        XDocument
            .Load(Path(relativePath))
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")!.Value.Replace('\\', '/'))
            .ToArray();

    private static string Path(string relativePath) =>
        System.IO.Path.Combine(
            _root,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)
        );

    private static IEnumerable<string> SourceLines(string relativePath) =>
        Directory
            .EnumerateFiles(Path(relativePath), "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains(
                    $"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}"
                )
                && !file.Contains(
                    $"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}"
                )
            )
            .SelectMany(File.ReadLines);
}
