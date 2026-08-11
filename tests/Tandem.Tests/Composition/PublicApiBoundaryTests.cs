using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using FluentAssertions;

namespace Tandem.Tests.Composition;

public sealed class PublicApiBoundaryTests
{
    private static readonly NullabilityInfoContext _nullability = new();
    private static readonly string _root = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
    );

    [Theory]
    [InlineData(
        typeof(Pipeline<>),
        "src/Tandem/ExportedApi.txt",
        "src/Tandem/PublicApiMembers.txt"
    )]
    [InlineData(
        typeof(PipelineOperation),
        "src/Tandem.Advanced/ExportedApi.txt",
        "src/Tandem.Advanced/PublicApiMembers.txt"
    )]
    [InlineData(
        typeof(Tandem.Ledger.SqliteLedgerStore),
        "src/Tandem.Ledger/ExportedApi.txt",
        "src/Tandem.Ledger/PublicApiMembers.txt"
    )]
    [InlineData(
        typeof(Tandem.Terminal.TerminalPipelineDisplay),
        "src/Tandem.Terminal/ExportedApi.txt",
        "src/Tandem.Terminal/PublicApiMembers.txt"
    )]
    public void PublicApi_MatchesReviewedManifest(
        Type assemblyMarker,
        string typeManifestPath,
        string memberManifestPath
    )
    {
        var manifest = ReadManifest(typeManifestPath);
        var exported = assemblyMarker
            .Assembly.GetExportedTypes()
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        manifest.Values.SelectMany(types => types).Should().OnlyHaveUniqueItems();
        exported
            .Should()
            .Equal(manifest.Values.SelectMany(types => types).Order(StringComparer.Ordinal));

        var members = RenderPublicApi(assemblyMarker.Assembly, manifest);
        var path = Path.Combine(_root, memberManifestPath);
        if (Environment.GetEnvironmentVariable("TANDEM_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllLines(path, members);
        }

        File.ReadAllLines(path).Should().Equal(members);
    }

    private static string[] RenderPublicApi(
        Assembly assembly,
        IReadOnlyDictionary<string, IReadOnlyList<string>> categories
    )
    {
        var lines = new List<string>();
        foreach (var category in categories)
        {
            lines.Add($"[{category.Key}]");
            foreach (var name in category.Value.Order(StringComparer.Ordinal))
            {
                var type = assembly.GetType(name, throwOnError: true)!;
                lines.Add($"T {FormatType(type)}{FormatConstraints(type.GetGenericArguments())}");
                lines.AddRange(
                    type.GetConstructors(
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                        )
                        .Select(constructor => $"C {FormatParameters(constructor.GetParameters())}")
                        .Order(StringComparer.Ordinal)
                );
                lines.AddRange(
                    type.GetMethods(
                            BindingFlags.Public
                                | BindingFlags.Instance
                                | BindingFlags.Static
                                | BindingFlags.DeclaredOnly
                        )
                        .Where(method => !method.IsSpecialName)
                        .Select(method =>
                            $"M {(method.IsStatic ? "static " : "")}{FormatNullableType(method.ReturnType, _nullability.Create(method.ReturnParameter))} {method.Name}"
                            + $"{FormatGenericParameters(method)}{FormatParameters(method.GetParameters())}"
                            + FormatConstraints(method.GetGenericArguments())
                        )
                        .Order(StringComparer.Ordinal)
                );
                lines.AddRange(
                    type.GetProperties(
                            BindingFlags.Public
                                | BindingFlags.Instance
                                | BindingFlags.Static
                                | BindingFlags.DeclaredOnly
                        )
                        .Select(property =>
                            $"P {FormatNullableType(property.PropertyType, _nullability.Create(property))} {property.Name}"
                            + (
                                property.GetIndexParameters().Length == 0
                                    ? ""
                                    : FormatParameters(property.GetIndexParameters())
                            )
                            + $" {{{(property.GetMethod?.IsPublic == true ? " get;" : "")}"
                            + $"{(property.SetMethod?.IsPublic == true ? " set;" : "")} }}"
                        )
                        .Order(StringComparer.Ordinal)
                );
                lines.AddRange(
                    type.GetFields(
                            BindingFlags.Public
                                | BindingFlags.Instance
                                | BindingFlags.Static
                                | BindingFlags.DeclaredOnly
                        )
                        .Select(field =>
                            $"F {(field.IsStatic ? "static " : "")}{FormatType(field.FieldType)} {field.Name}"
                        )
                        .Order(StringComparer.Ordinal)
                );
                lines.AddRange(
                    type.GetEvents(
                            BindingFlags.Public
                                | BindingFlags.Instance
                                | BindingFlags.Static
                                | BindingFlags.DeclaredOnly
                        )
                        .Select(@event => $"E {FormatType(@event.EventHandlerType!)} {@event.Name}")
                        .Order(StringComparer.Ordinal)
                );
            }
            lines.Add("");
        }
        if (lines.Count > 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines.ToArray();
    }

    private static string FormatGenericParameters(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? $"<{string.Join(",", method.GetGenericArguments().Select(argument => argument.Name))}>"
            : "";

    private static string FormatParameters(ParameterInfo[] parameters) =>
        $"({string.Join(", ", parameters.Select(FormatParameter))})";

    private static string FormatParameter(ParameterInfo parameter)
    {
        var modifier =
            parameter.GetCustomAttribute<ParamArrayAttribute>() is not null ? "params "
            : parameter.ParameterType.IsByRef && parameter.IsOut ? "out "
            : parameter.ParameterType.IsByRef && parameter.IsIn ? "in "
            : parameter.ParameterType.IsByRef ? "ref "
            : "";
        var type = parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()!
            : parameter.ParameterType;
        return $"{modifier}{FormatNullableType(type, _nullability.Create(parameter))} {parameter.Name}{FormatDefault(parameter)}";
    }

    private static string FormatDefault(ParameterInfo parameter)
    {
        if (!parameter.IsOptional)
        {
            return "";
        }
        var value = parameter.DefaultValue;
        if (value is null)
        {
            return parameter.ParameterType.IsValueType ? " = default" : " = null";
        }
        if (value is DBNull or Missing)
        {
            return " = default";
        }
        return value switch
        {
            string text => $" = \"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            char character => $" = '{character}'",
            bool boolean => boolean ? " = true" : " = false",
            Enum enumeration => $" = {FormatType(enumeration.GetType())}.{enumeration}",
            IFormattable formattable =>
                $" = {formattable.ToString(null, CultureInfo.InvariantCulture)}",
            _ => $" = {value}",
        };
    }

    private static string FormatNullableType(Type type, NullabilityInfo nullability)
    {
        if (type.IsByRef)
        {
            return FormatNullableType(type.GetElementType()!, nullability);
        }
        if (type.IsArray)
        {
            var element = nullability.ElementType is { } elementNullability
                ? FormatNullableType(type.GetElementType()!, elementNullability)
                : FormatType(type.GetElementType()!);
            return $"{element}[{new string(',', type.GetArrayRank() - 1)}]"
                + (nullability.ReadState == NullabilityState.Nullable ? "?" : "");
        }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var name = (definition.FullName ?? definition.Name).Split('`')[0];
            var arguments = type.GetGenericArguments();
            var formatted = arguments.Select(
                (argument, index) =>
                    index < nullability.GenericTypeArguments.Length
                        ? FormatNullableType(argument, nullability.GenericTypeArguments[index])
                        : FormatType(argument)
            );
            return $"{name}<{string.Join(",", formatted)}>"
                + (nullability.ReadState == NullabilityState.Nullable ? "?" : "");
        }
        return FormatType(type)
            + (
                nullability.ReadState == NullabilityState.Nullable
                && !type.IsValueType
                && !type.IsGenericParameter
                    ? "?"
                    : ""
            );
    }

    private static string FormatConstraints(Type[] parameters)
    {
        var constraints = parameters
            .Where(parameter => parameter.IsGenericParameter)
            .Select(parameter =>
            {
                var values = new List<string>();
                var attributes = parameter.GenericParameterAttributes;
                if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                {
                    values.Add("class");
                }
                if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                {
                    values.Add("struct");
                }
                values.AddRange(parameter.GetGenericParameterConstraints().Select(FormatType));
                if (
                    (attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0
                    && (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0
                )
                {
                    values.Add("new()");
                }
                return values.Count == 0
                    ? ""
                    : $" where {parameter.Name} : {string.Join(", ", values)}";
            });
        return string.Concat(constraints);
    }

    private static string FormatType(Type type)
    {
        if (type.IsByRef)
        {
            return $"ref {FormatType(type.GetElementType()!)}";
        }
        if (type.IsArray)
        {
            return $"{FormatType(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        }
        if (type.IsGenericParameter)
        {
            return type.Name;
        }
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var definition = type.GetGenericTypeDefinition();
        var name = (definition.FullName ?? definition.Name).Split('`')[0];
        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FormatType))}>";
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
        var assemblies = new[] { typeof(Pipeline<>).Assembly, typeof(PipelineOperation).Assembly };
        var exported = assemblies.SelectMany(assembly => assembly.GetExportedTypes()).ToArray();

        exported.Should().NotContain(type => IsInfrastructureNamespace(type.Namespace));
        typeof(PipelineOperation)
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
            || assembly is "OpenAI" or "Microsoft.Extensions.AI.OpenAI";
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
