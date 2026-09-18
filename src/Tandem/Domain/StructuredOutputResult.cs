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

    public string CorrectionPrompt(JsonElement? schema)
    {
        var problems = string.Join(
            Environment.NewLine,
            Problems.Select(problem => $"- {problem.Field}: {problem.Message}")
        );
        return $"""
            Your previous response could not be accepted:

            {problems}

            {AgentStructuredOutputPrompt.Schema(schema)}

            Reply with only the corrected JSON object.
            """;
    }
}

internal static class AgentStructuredOutputPrompt
{
    public static string Initial<TState>(AgentStructuredOutputDescriptor<TState> output) =>
        $"""
            {output.Instructions}

            {Schema(output.JsonSchema)}
            """;

    public static string Schema(JsonElement? schema) =>
        schema is null
            ? ""
            : $"""
                Return a JSON object matching this schema:
                {schema.Value.GetRawText()}
                """;
}
