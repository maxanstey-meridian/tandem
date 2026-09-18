using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Tandem.Infrastructure;

internal sealed class ToolInputException(string message) : ArgumentException(message);

internal sealed class WorkspacePathException(string message) : UnauthorizedAccessException(message);

internal static class ToolInputValidation
{
    internal static void ValidateArguments(AIFunction function, AIFunctionArguments arguments)
    {
        var schema = function.JsonSchema;
        if (!schema.TryGetProperty("properties", out var properties))
        {
            return;
        }

        if (
            schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind != JsonValueKind.False
        )
        {
            return;
        }

        var names = properties
            .EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = arguments.Keys.Where(key => !names.Contains(key)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ToolInputException(
                $"Unknown arguments: {string.Join(", ", unknown)}. Supported arguments: {string.Join(", ", names)}. Correct the call; it was not executed."
            );
        }
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(value => value.GetString()!))
            {
                if (!arguments.ContainsKey(name))
                {
                    throw new ToolInputException(
                        $"Missing required argument '{name}'. Correct the call; it was not executed."
                    );
                }
            }
        }
        foreach (var argument in arguments)
        {
            if (
                properties.TryGetProperty(argument.Key, out var property)
                && !MatchesType(JsonSerializer.SerializeToElement(argument.Value), property)
            )
            {
                throw new ToolInputException(
                    $"Argument '{argument.Key}' has the wrong JSON type. Expected {property}. Correct the call; it was not executed."
                );
            }
        }
    }

    private static bool MatchesType(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("anyOf", out var alternatives))
        {
            return alternatives.EnumerateArray().Any(option => MatchesType(value, option));
        }

        if (!schema.TryGetProperty("type", out var type))
        {
            return true;
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(option => MatchesName(value, option.GetString()));
        }

        return MatchesName(value, type.GetString());
    }

    private static bool MatchesName(JsonElement value, string? type) =>
        type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        };

    internal static bool IsExpected(string name, Exception exception) =>
        exception is ToolInputException or WorkspacePathException
        || (
            (
                name.StartsWith("file_access_", StringComparison.Ordinal)
                || name.StartsWith("git_", StringComparison.Ordinal)
                || name.StartsWith("run_command_", StringComparison.Ordinal)
                || name.StartsWith("run_verification_", StringComparison.Ordinal)
            ) && exception is ArgumentException or JsonException
        )
        || (
            name.StartsWith("file_access_", StringComparison.Ordinal)
            && exception is FileNotFoundException or DirectoryNotFoundException
        )
        || (name == "file_access_grep" && exception is RegexMatchTimeoutException);

    internal static JsonElement Error(string message) =>
        JsonSerializer.SerializeToElement(
            new
            {
                isError = true,
                code = "invalid_tool_input",
                message,
            }
        );
}
