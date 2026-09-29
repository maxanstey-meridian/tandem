using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem.Infrastructure;

/// <summary>
/// The one model-facing tool error: <c>{ isError, code, message, problems, details? }</c>. The
/// model can correct the call in the same session; nothing was accepted or executed.
/// </summary>
internal sealed record ToolError(
    string Code,
    string Message,
    IReadOnlyList<ValidationProblem> Problems,
    JsonElement? Details = null
)
{
    private static readonly JsonSerializerOptions _options = new(TandemJson.TypedContract)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ToolError InvalidCall(
        string toolName,
        IReadOnlyList<ValidationProblem> problems
    ) => new("invalid_tool_call", $"invalid {toolName} call", problems);

    public JsonElement ToJson() =>
        JsonSerializer.SerializeToElement(
            new Wire(true, Code, Message, Problems, Details),
            _options
        );

    private sealed record Wire(
        bool IsError,
        string Code,
        string Message,
        IReadOnlyList<ValidationProblem> Problems,
        JsonElement? Details
    );
}
