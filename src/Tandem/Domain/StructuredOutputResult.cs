using System.Text.Json;

namespace Tandem.Domain;

internal sealed record AgentStructuredOutputProblem(string Field, string Message);

internal sealed record AgentStructuredOutcome<TState>(
    string Kind,
    string Summary,
    JsonElement Payload,
    TState? UpdatedState = default
);

internal sealed record AgentStructuredOutputResult<TState>(
    AgentStructuredOutcome<TState>? Outcome,
    IReadOnlyList<AgentStructuredOutputProblem> Problems,
    string RawResponse,
    object? Candidate = null
)
{
    public bool Success => Outcome is not null;

    public string CorrectionPrompt()
    {
        var problems = string.Join(
            Environment.NewLine,
            Problems.Select(problem => $"- {problem.Field}: {problem.Message}")
        );
        return $"""
            Your previous response could not be accepted:

            {problems}

            Reply with only the corrected JSON object.
            """;
    }
}
