using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
        var advanced = ProjectReferences("src/Tandem.Advanced/Tandem.Advanced.csproj");
        var delivery = ProjectReferences("src/Tandem.Delivery/Tandem.Delivery.csproj");
        var tool = ProjectReferences("src/Tandem.Tool/Tandem.Tool.csproj");
        var debate = ProjectReferences("samples/Tandem.Sample.Debate/Tandem.Sample.Debate.csproj");
        var support = ProjectReferences(
            "samples/Tandem.Sample.Support/Tandem.Sample.Support.csproj"
        );
        var songwriter = ProjectReferences(
            "samples/Tandem.Sample.Songwriter/Tandem.Sample.Songwriter.csproj"
        );

        tandem.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
        tandem.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        advanced.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        delivery.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        delivery.Should().Contain(reference => reference.EndsWith("Tandem.Advanced.csproj"));
        tool.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        tool.Should().Contain(reference => reference.EndsWith("Tandem.Delivery.csproj"));
        debate.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        debate.Should().Contain(reference => reference.EndsWith("Tandem.Advanced.csproj"));
        debate.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
        support.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        support.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        support.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
        songwriter.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        songwriter.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        songwriter.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
    }

    [Fact]
    public void Support_IsAnUnprivilegedConsumerWithoutCodingOrRuntimePlumbing()
    {
        var project = File.ReadAllText(
            Path("samples/Tandem.Sample.Support/Tandem.Sample.Support.csproj")
        );
        project.Should().NotContain("Microsoft.Agents");
        project.Should().NotContain("Tandem.Delivery");
        project.Should().NotContain("Compile Include");
        project.Should().NotContain("InternalsVisibleTo");

        var source = string.Join('\n', SourceLines("samples/Tandem.Sample.Support"));
        source.Should().NotContain("using Microsoft.Agents");
        source.Should().NotContain("Tandem.Delivery");
        source.Should().NotContain("using Tandem.Infrastructure");
        source.Should().NotContain("System.Reflection");
        source.Should().NotContain("InternalsVisibleTo");
        source.Should().NotContain("WorkspacePath");
        source.Should().NotContain("WithWorkspace");
        source.Should().NotContain("IPipelineExecutionContext");
        source.Should().NotContain("QueueStateUpdateAsync");
        source.Should().NotContain("ReadStateAsync");
        source.Should().NotContain("PipelineBuildContext");
        source.Should().NotContain("ChatOptions");
        source.Should().NotContain("ChatResponseFormat");
        source.Should().NotContain(".Request");
        source.Should().NotContain(".Port");
        source.Should().NotContain(".Resume");
        source.Should().NotContain("IRawPipelineNode");
        source.Should().NotContain("class ClassifyTicketAgent");
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
        source.Should().NotContain(line => line.Contains("using Tandem.Infrastructure"));
        source.Should().NotContain(line => line.Contains("System.Reflection"));
        source.Should().NotContain(line => line.Contains("InternalsVisibleTo"));
        source.Should().NotContain(line => line.Contains("WorkspacePath"));
        source.Should().NotContain(line => line.Contains("ModelContextProtocol"));
        source.Should().NotContain(line => line.Contains("PipelineBuildContext"));
        source.Should().NotContain(line => line.Contains("ChatOptions"));
        source.Should().NotContain(line => line.Contains("ChatResponseFormat"));
        source.Should().NotContain(line => line.Contains("ReleaseUsage"));
        source.Should().NotContain(line => line.Contains("IRawPipelineNode"));
        source.Should().NotContain(line => line.Contains("class ProposerAgent"));
        source.Should().NotContain(line => line.Contains("class JudgeAgent"));
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
            .SelectMany(File.ReadLines)
            .ToArray();
        source.Should().NotContain(line => line.Contains("using Microsoft.Agents"));
        source.Should().NotContain(line => line.Contains("using Tandem.Infrastructure"));
        source.Should().NotContain(line => line.Contains("namespace Tandem.Infrastructure"));
    }

    [Fact]
    public void ConsumerProjects_ImportNoMafNamespaces()
    {
        SourceLines("src/Tandem.Delivery")
            .Concat(SourceLines("samples/Tandem.Sample.Debate"))
            .Concat(SourceLines("samples/Tandem.Sample.Support"))
            .Concat(SourceLines("samples/Tandem.Sample.Songwriter"))
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
    public void AuthoredSteps_UseOnlyCanonicalGeneratedOutcomes()
    {
        foreach (
            var root in new[]
            {
                "src/Tandem.Delivery",
                "samples/Tandem.Sample.Debate",
                "samples/Tandem.Sample.Support",
                "samples/Tandem.Sample.Songwriter",
            }
        )
        {
            var source = string.Join('\n', SourceLines(root));
            source.Should().NotContain("class ResultCase");
            source.Should().NotContain("record ResultCase");
            source.Should().NotContain("ExecutorBinding");
        }

        var songwriter = File.ReadAllText(
            Path("samples/Tandem.Sample.Songwriter/SongwriterSteps.cs")
        );
        songwriter.Should().Contain("AgentDefinition<SongwriterState> Songwriter");
        songwriter.Should().NotContain("class SongwriterAgent");
        songwriter.Should().NotContain("IRawPipelineNode");
        File.ReadAllText(Path("samples/Tandem.Sample.Songwriter/SongwriterDefinitions.cs"))
            .Should()
            .Contain("PipelineNodes.Complete<SongwriterState>");
        songwriter.Should().NotContain("[Union");
        File.ReadAllText(Path("samples/Tandem.Sample.Songwriter/SongwriterComposition.cs"))
            .Should()
            .NotContain(".Result.");

        var generator = File.ReadAllText(Path("src/Tandem.Generators/PipelineStepGenerator.cs"));
        generator.Should().Contain("GeneratedPassThroughStepDescriptor");
        generator.Should().Contain("GeneratedStateStepDescriptor");
        generator.Should().Contain("GeneratedOutcomeStepDescriptor");
        generator.Should().NotContain("GeneratedCustomStepDescriptor");
        generator.Should().NotContain("GetMembers(\"Runtime\")");
        generator.Should().NotContain("GetMembers(\"Outcome\")");

        var deliverySteps = File.ReadAllText(Path("src/Tandem.Delivery/DeliverySteps.cs"));
        deliverySteps.Should().Contain("IPipelineNode<DeliveryState> CompleteRun");
        deliverySteps.Should().Contain("IPipelineNode<DeliveryState> FailRun");
        deliverySteps.Should().NotContain("IRawPipelineNode");
        deliverySteps.Should().NotContain("AdvancedPipelineNodes.Stage");
    }

    [Fact]
    public void OrdinaryAgentAndNodeApi_HidesInfrastructureAuthoring()
    {
        var assembly = typeof(AgentRuntime).Assembly;
        var exportedNames = assembly.GetExportedTypes().Select(type => type.Name).ToArray();

        exportedNames.Should().NotContain("AgentOperation`1");
        exportedNames.Should().NotContain("AgentOutput`1");
        exportedNames.Should().NotContain("CapabilityReceipt");
        exportedNames.Should().NotContain("AgentCapability`1");
        exportedNames.Should().NotContain("CheckpointPolicy`1");
        exportedNames.Should().NotContain("AgentTurnPolicy`1");
        exportedNames.Should().NotContain("AgentConversationDecision");
        exportedNames.Should().NotContain("ToolInterceptionResult");
        exportedNames.Should().NotContain("IRawPipelineNode");
        typeof(OperationResult<>).Namespace.Should().Be("Tandem.Advanced");
        typeof(AgentDefinition<>).GetProperty("Operation").Should().BeNull();
        typeof(AgentCapabilities)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(AgentCapabilities.Create))
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Should()
            .NotContain(typeof(IServiceCollection));
        typeof(IGeneratedPipelineStep<DeliveryState, string>)
            .GetInterfaces()
            .Should()
            .Contain(typeof(IPipelineNode<DeliveryState>));

        var pipelineMethods = typeof(PipelineBuilder<DeliveryState>).GetMethods(
            BindingFlags.Public | BindingFlags.Instance
        );
        pipelineMethods
            .Single(method => method.Name == nameof(PipelineBuilder<DeliveryState>.Build))
            .GetParameters()
            .Single()
            .ParameterType.Should()
            .Be(typeof(IPipelineNode<DeliveryState>[]));
        var ordinaryMethods = typeof(AgentBuilder<>).GetMethods(
            BindingFlags.Public | BindingFlags.Instance
        );
        ordinaryMethods
            .Select(method => method.Name)
            .Should()
            .NotContain([
                "WithMessageFromContext",
                "WithStructuredOutput",
                "WithLifecycleActions",
                "WithCheckpoint",
                "WithMessageAugmentation",
                "WithContinuationPolicy",
            ]);
    }

    [Fact]
    public void SessionContinuation_IsExplicitAndOrdinaryAgentsDefaultFresh()
    {
        var source = File.ReadAllText(Path("src/Tandem.Delivery/DeliveryStepsFactory.cs"));
        source.Should().Contain("continueSession: true");
        source.Should().Contain("builder.ContinueSession()");
        source.Should().NotContain("WithSessionPolicy");

        var debate = string.Join('\n', SourceLines("samples/Tandem.Sample.Debate"));
        debate.Should().Contain(".ContinueSession()");
        debate.Should().NotContain("WithSessionPolicy");
        var support = string.Join('\n', SourceLines("samples/Tandem.Sample.Support"));
        support.Should().NotContain("ContinueSession");
        support.Should().NotContain("WithSessionPolicy");
        var songwriter = string.Join('\n', SourceLines("samples/Tandem.Sample.Songwriter"));
        songwriter.Should().NotContain("ContinueSession");
        songwriter.Should().NotContain("WithSessionPolicy");
    }

    [Fact]
    public void AuthoredStepResults_ContainNoExecutionEnvelopePlumbing()
    {
        var source = new[]
        {
            "src/Tandem.Delivery/DeliverySteps.cs",
            "samples/Tandem.Sample.Debate/DebateSteps.cs",
            "samples/Tandem.Sample.Support/SupportSteps.cs",
            "samples/Tandem.Sample.Songwriter/SongwriterSteps.cs",
        }
            .Select(Path)
            .Select(File.ReadAllText)
            .ToArray();

        source
            .Should()
            .NotContain(text => text.Contains("PipelineRuntime", StringComparison.Ordinal));
        source.Should().NotContain(text => text.Contains("BlockOutcome", StringComparison.Ordinal));
        source
            .Should()
            .NotContain(text => text.Contains("LatestOutcome", StringComparison.Ordinal));
        source.Should().NotContain(text => text.Contains("LatestResult", StringComparison.Ordinal));
        source
            .Should()
            .NotContain(text => text.Contains("PipelineStepResult", StringComparison.Ordinal));
    }

    [Fact]
    public void PipelineAuthoring_UsesMafOrderedSwitchForSemanticRoutes()
    {
        var source = File.ReadAllText(Path("src/Tandem/Authoring/PipelineStep.cs"));
        source.Should().Contain("_builder.AddSwitch");
        source.Should().Contain("PipelineRouteRegistration");
        source.Should().NotContain("RouteDefinition");
    }

    [Fact]
    public void PublicTandemApi_ExposesNoMafTypes()
    {
        var leaks = typeof(Pipeline<>)
            .Assembly.GetExportedTypes()
            .SelectMany(PublicSurfaceTypes)
            .Where(type => type.Assembly.GetName().Name?.StartsWith("Microsoft.Agents") == true)
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        leaks.Should().BeEmpty();
    }

    [Fact]
    public void Tool_UsesOneProcessOwnedRuntimeAndPersistsPublicationMetadata()
    {
        var source = File.ReadAllText(Path("src/Tandem.Tool/Program.cs"));
        var persistence = source.IndexOf("RunProjection.Initial(", StringComparison.Ordinal);
        var publication = source.IndexOf(
            "Console.WriteLine($\"Run:       {runPaths.RunId}\")",
            StringComparison.Ordinal
        );

        persistence.Should().BeGreaterThan(-1);
        publication.Should().BeGreaterThan(persistence);
        source.Should().Contain("new PipelineRunner()");
        source.Should().NotContain("InProcessPipelineRunner");
        source.Should().NotContain("PendingExternalRequest");
        source.Should().NotContain("ExternalRequestAnswer");
        source.Should().NotContain("attachCommand");
        source.Should().NotContain("DurableTask");
        source.Should().NotContain("TaskHub");
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

    private static IEnumerable<Type> PublicSurfaceTypes(Type type)
    {
        yield return type;

        if (type.BaseType is { } baseType)
        {
            foreach (var candidate in Expand(baseType))
            {
                yield return candidate;
            }
        }

        foreach (var contract in type.GetInterfaces())
        {
            foreach (var candidate in Expand(contract))
            {
                yield return candidate;
            }
        }

        foreach (
            var parameter in type.GetGenericArguments()
                .Where(argument => argument.IsGenericParameter)
        )
        {
            foreach (var constraint in parameter.GetGenericParameterConstraints())
            {
                foreach (var candidate in Expand(constraint))
                {
                    yield return candidate;
                }
            }
        }

        foreach (
            var memberType in type.GetConstructors()
                .SelectMany(constructor =>
                    constructor.GetParameters().Select(parameter => parameter.ParameterType)
                )
                .Concat(type.GetMethods().Select(method => method.ReturnType))
                .Concat(
                    type.GetMethods()
                        .SelectMany(method =>
                            method.GetParameters().Select(parameter => parameter.ParameterType)
                        )
                )
                .Concat(type.GetProperties().Select(property => property.PropertyType))
                .Concat(type.GetEvents().Select(@event => @event.EventHandlerType!))
                .Concat(type.GetFields().Select(field => field.FieldType))
                .Where(candidate => candidate is not null)
        )
        {
            foreach (var candidate in Expand(memberType))
            {
                yield return candidate;
            }
        }

        foreach (var method in type.GetMethods())
        {
            foreach (
                var parameter in method
                    .GetGenericArguments()
                    .Where(argument => argument.IsGenericParameter)
            )
            {
                foreach (var constraint in parameter.GetGenericParameterConstraints())
                {
                    foreach (var candidate in Expand(constraint))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var candidate in Expand(element))
            {
                yield return candidate;
            }
        }
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var candidate in Expand(argument))
            {
                yield return candidate;
            }
        }
    }
}
