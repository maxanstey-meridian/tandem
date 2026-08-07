using Tandem.Domain;

namespace Tandem;

public static class StructuredOutputAcceptancePolicies
{
    public static StructuredOutputAcceptancePolicy<TState> RequireToolCallWhen<TState>(
        Func<StructuredOutputResult<TState>, bool> requiresToolCall,
        Func<string, bool>? acceptsTool = null,
        string? correction = null
    )
    {
        return observation =>
        {
            if (!requiresToolCall(observation.Result))
            {
                return [];
            }

            var accepted = acceptsTool ?? (_ => true);
            if (observation.ToolNames.Any(accepted))
            {
                return [];
            }

            return
            [
                new StructuredOutputProblem(
                    "$grounding",
                    correction
                        ?? "This decision requires a supporting tool call before it can be accepted."
                ),
            ];
        };
    }
}
