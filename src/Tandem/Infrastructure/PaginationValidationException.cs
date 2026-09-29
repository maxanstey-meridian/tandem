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
    internal ToolError Error { get; } =
        new(
            "invalid_pagination",
            message,
            [new ValidationProblem(parameter, message)],
            JsonSerializer.SerializeToElement(details)
        );
}
