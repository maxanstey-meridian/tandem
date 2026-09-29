using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation.Results;

namespace Tandem.Infrastructure;

internal sealed record ToolProblem(string? Field, string Message)
{
    public static IReadOnlyList<ToolProblem> From(ValidationResult validation) =>
        [.. validation.Errors.Select(error => new ToolProblem(FieldOf(error), error.ErrorMessage))];

    public static string FieldOf(ValidationFailure failure) =>
        string.IsNullOrEmpty(failure.PropertyName)
            ? failure.PropertyName
            : char.ToLowerInvariant(failure.PropertyName[0]) + failure.PropertyName[1..];
}

/// <summary>
/// The one model-facing tool error: <c>{ isError, code, message, problems }</c>. The model can
/// correct the call in the same session; nothing was accepted or executed.
/// </summary>
internal sealed record ToolError(string Code, string Message, IReadOnlyList<ToolProblem> Problems)
{
    private static readonly JsonSerializerOptions _options = new(TandemJson.TypedContract)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ToolError InvalidCall(string toolName, IReadOnlyList<ToolProblem> problems) =>
        new("invalid_tool_call", $"invalid {toolName} call", problems);

    public JsonElement ToJson() =>
        JsonSerializer.SerializeToElement(new Wire(true, Code, Message, Problems), _options);

    private sealed record Wire(
        bool IsError,
        string Code,
        string Message,
        IReadOnlyList<ToolProblem> Problems
    );
}
