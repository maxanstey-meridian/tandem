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
    }

    internal static bool IsExpected(Exception exception, ToolSemantics? semantics = null) =>
        exception is ToolInputException or WorkspacePathException
        || (
            semantics?.Effect is ToolEffect.Read or ToolEffect.WorkspaceMutation
            && exception
                is ArgumentException
                    or JsonException
                    or FileNotFoundException
                    or DirectoryNotFoundException
                    or RegexMatchTimeoutException
        );

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
