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
    public void ProjectReferences_PreserveSdkLayering()
    {
        var tandem = ProjectReferences("src/Tandem/Tandem.csproj");
        var advanced = ProjectReferences("src/Tandem.Advanced/Tandem.Advanced.csproj");
        var ledger = ProjectReferences("src/Tandem.Ledger/Tandem.Ledger.csproj");
        var packets = ProjectReferences("src/Tandem.Packets/Tandem.Packets.csproj");
        var debate = ProjectReferences("examples/debate/csharp/Tandem.Sample.Debate.csproj");
        var codeWriter = ProjectReferences(
            "examples/code-writer/csharp/Tandem.Sample.CodeWriter.csproj"
        );
        var songwriter = ProjectReferences(
            "examples/songwriter/csharp/Tandem.Sample.Songwriter.csproj"
        );

        tandem.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
        tandem.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        advanced.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        ledger.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        debate.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        debate.Should().Contain(reference => reference.EndsWith("Tandem.Advanced.csproj"));
        debate.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
        codeWriter.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        codeWriter.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        codeWriter.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
        songwriter.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        songwriter.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        songwriter.Should().NotContain(reference => reference.Contains("Tandem.Delivery"));
        tandem.Should().NotContain(reference => reference.Contains("Tandem.Ledger"));
        advanced.Should().NotContain(reference => reference.Contains("Tandem.Ledger"));
        packets.Should().BeEmpty();
        codeWriter.Should().NotContain(reference => reference.Contains("Tandem.Ledger"));
        songwriter.Should().NotContain(reference => reference.Contains("Tandem.Ledger"));
    }

    [Fact]
    public void CodeWriter_IsAnUnprivilegedConsumerWithoutRuntimePlumbing()
    {
        var project = File.ReadAllText(
            Path("examples/code-writer/csharp/Tandem.Sample.CodeWriter.csproj")
        );
        project.Should().NotContain("Microsoft.Agents");
        project.Should().NotContain("Tandem.Delivery");
        project.Should().NotContain("Compile Include");
        project.Should().NotContain("InternalsVisibleTo");

        var source = string.Join('\n', SourceLines("examples/code-writer/csharp"));
        source.Should().NotContain("using Microsoft.Agents");
        source.Should().NotContain("Tandem.Delivery");
        source.Should().NotContain("using Tandem.Infrastructure");
        source.Should().NotContain("System.Reflection");
        source.Should().NotContain("InternalsVisibleTo");
        source.Should().NotContain("WorkspacePath");
        source.Should().NotContain("WithWorkspace");
        source.Should().NotContain("IPipelineExecutionContext");
        source.Should().NotContain("PipelineBuildContext");
        source.Should().NotContain("ChatOptions");
        source.Should().NotContain("ChatResponseFormat");
        source.Should().NotContain("IRawPipelineNode");
        source.Should().NotContain("class ImplementerAgent");
        source.Should().NotContain("class ReviewerAgent");
    }

    [Fact]
    public void Debate_IsAnUnprivilegedConsumerWithoutMafOrDeliveryVocabulary()
    {
        var project = File.ReadAllText(Path("examples/debate/csharp/Tandem.Sample.Debate.csproj"));
        project.Should().NotContain("Microsoft.Agents");
        project.Should().NotContain("Tandem.Delivery");
        project.Should().NotContain("Compile Include");
        project.Should().NotContain("InternalsVisibleTo");

        var source = Directory
            .EnumerateFiles(Path("examples/debate/csharp"), "*.cs", SearchOption.AllDirectories)
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
            .SelectMany(File.ReadLines);
        source.Should().NotContain(line => line.Contains("Debate", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsumerProjects_ImportNoMafNamespaces()
    {
        SourceLines("examples/debate/csharp")
            .Concat(SourceLines("examples/code-writer/csharp"))
            .Concat(SourceLines("examples/songwriter/csharp"))
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
                "examples/debate/csharp",
                "examples/code-writer/csharp",
                "examples/songwriter/csharp",
            }
        )
        {
            var source = string.Join('\n', SourceLines(root));
            source.Should().NotContain("class ResultCase");
            source.Should().NotContain("record ResultCase");
            source.Should().NotContain("ExecutorBinding");
        }

        var songwriter = File.ReadAllText(
            Path("examples/songwriter/csharp/SongwriterParticipants.cs")
        );
        songwriter.Should().Contain("AgentDefinition<SongwriterState> Songwriter");
        songwriter.Should().NotContain("class SongwriterAgent");
        songwriter.Should().NotContain("IRawPipelineNode");
        File.ReadAllText(Path("examples/songwriter/csharp/SongwriterDefinitions.cs"))
            .Should()
            .Contain("PipelineNodes.Complete(new SongwriterComplete())");
        songwriter.Should().NotContain("[Union");
        File.ReadAllText(Path("examples/songwriter/csharp/SongwriterComposition.cs"))
            .Should()
            .NotContain(".Result.");

        var generator = File.ReadAllText(Path("src/Tandem.Generators/PipelineStepGenerator.cs"));
        generator.Should().Contain("GeneratedPassThroughStepDescriptor");
        generator.Should().Contain("GeneratedStateStepDescriptor");
        generator.Should().Contain("GeneratedOutcomeStepDescriptor");
        generator.Should().NotContain("GeneratedCustomStepDescriptor");
        generator.Should().NotContain("GetMembers(\"Runtime\")");
        generator.Should().NotContain("GetMembers(\"Outcome\")");
    }

    [Fact]
    public void OrdinaryAgentAndNodeApi_HidesInfrastructureAuthoring()
    {
        var assembly = typeof(Agent).Assembly;
        var exportedNames = assembly.GetExportedTypes().Select(type => type.Name).ToArray();

        exportedNames.Should().NotContain("AgentOperation`1");
        exportedNames.Should().NotContain("AgentOutput`1");
        exportedNames.Should().NotContain("CapabilityReceipt");
        exportedNames.Should().Contain("AgentCapability`1");
        exportedNames.Should().Contain("AgentCapability`2");
        exportedNames.Should().NotContain("CheckpointPolicy`1");
        exportedNames.Should().NotContain("AgentTurnPolicy`1");
        exportedNames.Should().NotContain("AgentConversationDecision");
        exportedNames.Should().NotContain("ToolInterceptionResult");
        exportedNames.Should().NotContain("IRawPipelineNode");
        typeof(OperationResult<>).Namespace.Should().Be("Tandem.Advanced");
        typeof(AgentDefinition<>).GetProperty("Operation").Should().BeNull();
        typeof(AgentCapabilities)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == nameof(AgentCapabilities.Create)
                && method.GetParameters().Length == 2
            )
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Should()
            .NotContain(typeof(IServiceCollection));
        typeof(IGeneratedPipelineStep<BoundaryState, string>)
            .GetInterfaces()
            .Should()
            .Contain(typeof(IPipelineNode<BoundaryState>));

        var pipelineMethods = typeof(PipelineBuilder<BoundaryState>).GetMethods(
            BindingFlags.Public | BindingFlags.Instance
        );
        pipelineMethods
            .Single(method => method.Name == nameof(PipelineBuilder<BoundaryState>.Build))
            .GetParameters()
            .Single()
            .ParameterType.Should()
            .Be(typeof(IPipelineNode<BoundaryState>[]));
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
        var debate = string.Join('\n', SourceLines("examples/debate/csharp"));
        debate.Should().Contain(".ContinueSession()");
        debate.Should().NotContain("WithSessionPolicy");
        var codeWriter = string.Join('\n', SourceLines("examples/code-writer/csharp"));
        codeWriter.Should().Contain(".ContinueSession()");
        codeWriter.Should().NotContain("WithSessionPolicy");
        var songwriter = string.Join('\n', SourceLines("examples/songwriter/csharp"));
        songwriter.Should().NotContain("ContinueSession");
        songwriter.Should().NotContain("WithSessionPolicy");
    }

    [Fact]
    public void AuthoredStepResults_ContainNoExecutionEnvelopePlumbing()
    {
        var source = new[]
        {
            "examples/debate/csharp/DebateParticipants.cs",
            "examples/code-writer/csharp/CodeWriterParticipants.cs",
            "examples/songwriter/csharp/SongwriterParticipants.cs",
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

    private sealed record BoundaryState(string Value);
}
