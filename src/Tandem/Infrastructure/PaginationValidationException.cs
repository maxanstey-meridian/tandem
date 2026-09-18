using System.Text.Json;

namespace Tandem.Infrastructure;

// Only expected pagination validation failures may cross the tool boundary as
// recoverable feedback. Other argument, I/O and runtime exceptions remain faults.
internal sealed class PaginationValidationException(
    string parameter,
    string message,
    object details
) : ArgumentOutOfRangeException(parameter, message)
{
    internal JsonElement ToolResult { get; } =
        JsonSerializer.SerializeToElement(
            new
            {
                isError = true,
                code = "invalid_pagination",
                parameter,
                message,
                details,
            }
        );
}
