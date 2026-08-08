using System.ComponentModel;
using System.Reflection;
using FluentAssertions;

namespace Tandem.Tests.Composition;

public sealed class PublicApiBoundaryTests
{
    private static readonly string _root = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
    );

    [Theory]
    [InlineData(typeof(Pipeline<>), "src/Tandem/ExportedApi.txt")]
    [InlineData(typeof(AgentCapabilities), "src/Tandem.Advanced/ExportedApi.txt")]
    public void ExportedTypes_MatchReviewedManifest(Type assemblyMarker, string manifestPath)
    {
        var manifest = ReadManifest(manifestPath);
        var exported = assemblyMarker
            .Assembly.GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        manifest.Values.SelectMany(types => types).Should().OnlyHaveUniqueItems();
        exported
            .Should()
            .Equal(manifest.Values.SelectMany(types => types).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void GeneratorAbi_IsHiddenFromOrdinaryDiscovery()
    {
        var abi = ReadManifest("src/Tandem/ExportedApi.txt")["generator-abi"];

        foreach (var name in abi)
        {
            var type = typeof(Pipeline<>).Assembly.GetType(name, throwOnError: true)!;
            type.GetCustomAttribute<EditorBrowsableAttribute>()
                ?.State.Should()
                .Be(EditorBrowsableState.Never, $"{name} is generator ABI");
        }
    }

    [Fact]
    public void PublicPackages_ExposeNoInfrastructureNamespacesOrForbiddenSignatureTypes()
    {
        var assemblies = new[] { typeof(Pipeline<>).Assembly, typeof(AgentCapabilities).Assembly };
        var exported = assemblies.SelectMany(assembly => assembly.GetExportedTypes()).ToArray();

        exported.Should().NotContain(type => IsInfrastructureNamespace(type.Namespace));
        typeof(AgentCapabilities)
            .Assembly.GetExportedTypes()
            .Should()
            .OnlyContain(type => type.Namespace == "Tandem.Advanced");

        var forbidden = exported
            .SelectMany(PublicSurfaceTypes)
            .Where(IsForbidden)
            .Select(type => type.AssemblyQualifiedName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        forbidden.Should().BeEmpty();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadManifest(
        string relativePath
    )
    {
        var categories = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in File.ReadLines(Path.Combine(_root, relativePath)))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = [];
                categories.Add(line[1..^1], current);
                continue;
            }
            (current ?? throw new InvalidOperationException("Manifest entry has no category.")).Add(
                line
            );
        }
        return categories.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal
        );
    }

    private static bool IsForbidden(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? "";
        var ns = type.Namespace ?? "";
        return assembly.StartsWith("Microsoft.Agents", StringComparison.Ordinal)
            || assembly.StartsWith("ModelContextProtocol", StringComparison.Ordinal)
            || assembly.StartsWith("Spectre.Console", StringComparison.Ordinal)
            || assembly.StartsWith("YamlDotNet", StringComparison.Ordinal)
            || assembly.StartsWith("System.CommandLine", StringComparison.Ordinal)
            || assembly is "OpenAI" or "Microsoft.Extensions.AI.OpenAI"
            || ns.StartsWith("Tandem.Tool", StringComparison.Ordinal)
            || ns.StartsWith("Tandem.Delivery", StringComparison.Ordinal);
    }

    private static bool IsInfrastructureNamespace(string? value) =>
        value is not null && value.StartsWith("Tandem.Infrastructure", StringComparison.Ordinal);

    private static IEnumerable<Type> PublicSurfaceTypes(Type type)
    {
        foreach (var candidate in Expand(type))
        {
            yield return candidate;
        }
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
        foreach (var memberType in PublicMemberTypes(type))
        {
            foreach (var candidate in Expand(memberType))
            {
                yield return candidate;
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

    private static IEnumerable<Type> PublicMemberTypes(Type type) =>
        type.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters().Select(p => p.ParameterType))
            .Concat(type.GetMethods().Select(method => method.ReturnType))
            .Concat(
                type.GetMethods()
                    .SelectMany(method => method.GetParameters().Select(p => p.ParameterType))
            )
            .Concat(type.GetProperties().Select(property => property.PropertyType))
            .Concat(
                type.GetProperties()
                    .SelectMany(property =>
                        property.GetIndexParameters().Select(p => p.ParameterType)
                    )
            )
            .Concat(type.GetFields().Select(field => field.FieldType))
            .Concat(type.GetEvents().Select(@event => @event.EventHandlerType).OfType<Type>())
            .Concat(
                type.GetMethods()
                    .SelectMany(method =>
                        method
                            .GetGenericArguments()
                            .Where(argument => argument.IsGenericParameter)
                            .SelectMany(argument => argument.GetGenericParameterConstraints())
                    )
            );
}
