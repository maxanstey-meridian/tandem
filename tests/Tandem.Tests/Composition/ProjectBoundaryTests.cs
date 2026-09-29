using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;

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

        tandem.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        advanced.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        ledger.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        debate.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        debate.Should().Contain(reference => reference.EndsWith("Tandem.Advanced.csproj"));
        codeWriter.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        codeWriter.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        songwriter.Should().Contain(reference => reference.EndsWith("Tandem.csproj"));
        songwriter.Should().NotContain(reference => reference.Contains("Tandem.Advanced"));
        tandem.Should().NotContain(reference => reference.Contains("Tandem.Ledger"));
        advanced.Should().NotContain(reference => reference.Contains("Tandem.Ledger"));
        packets.Should().BeEmpty();
        codeWriter.Should().NotContain(reference => reference.Contains("Tandem.Ledger"));
        songwriter.Should().NotContain(reference => reference.Contains("Tandem.Ledger"));
    }

    [Theory]
    [InlineData(typeof(global::Examples.CodeWriter.CodeWriterComposition))]
    [InlineData(typeof(global::Examples.Debate.DebateComposition))]
    [InlineData(typeof(global::Examples.Songwriter.SongwriterComposition))]
    public void Examples_AreUnprivilegedConsumers(Type example)
    {
        var types = Types.InAssembly(example.Assembly);

        AssertRule(
            types
                .ShouldNot()
                .HaveDependencyOnAny(
                    "Microsoft.Agents",
                    "ModelContextProtocol",
                    "Tandem.Infrastructure",
                    "Microsoft.Extensions.AI.ChatOptions",
                    "Microsoft.Extensions.AI.ChatResponseFormat"
                )
        );
        AssertRule(types.ShouldNot().ResideInNamespaceStartingWith("Tandem"));
        AssertRule(types.That().AreClasses().ShouldNot().HaveNameEndingWith("Agent"));
    }

    [Fact]
    public void TandemCore_KnowsNothingAboutTheExamples()
    {
        var types = Types.InAssembly(typeof(Agent).Assembly);

        AssertRule(types.ShouldNot().HaveDependencyOn("Examples"));
        AssertRule(types.ShouldNot().HaveNameMatching("Debate"));
    }

    [Fact]
    public void OrdinaryAgentAndNodeApi_HidesInfrastructureAuthoring()
    {
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
                "WithLifecycleActions",
                "WithCheckpoint",
                "WithMessageAugmentation",
                "WithContinuationPolicy",
            ]);
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

    private static void AssertRule(ConditionList rule)
    {
        var result = rule.GetResult();
        result
            .IsSuccessful.Should()
            .BeTrue(
                "these types break the rule: {0}",
                string.Join(", ", result.FailingTypeNames ?? [])
            );
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
