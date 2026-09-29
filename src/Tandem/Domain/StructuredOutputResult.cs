using System.Text.Json;

namespace Tandem.Domain;

internal sealed record ParsedOutput<TOutput>(
    TOutput? Value,
    IReadOnlyList<ValidationProblem> Problems
)
{
    public static ParsedOutput<TOutput> Valid(TOutput value) => new(value, []);

    public static ParsedOutput<TOutput> Invalid(IReadOnlyList<ValidationProblem> problems) =>
        new(default, problems);

    public static ParsedOutput<TOutput> Invalid(string path, string message) =>
        Invalid([new ValidationProblem(path, message)]);
}

internal sealed record AgentStructuredOutputResult<TState>(
    IReadOnlyList<ValidationProblem> Problems,
    string RawResponse,
    JsonElement Payload = default,
    TState? UpdatedState = default
)
{
    public bool Success => Problems.Count == 0;

    public static AgentStructuredOutputResult<TState> Rejected(
        IReadOnlyList<ValidationProblem> problems,
        string rawResponse
    ) => new(problems, rawResponse);

    public string CorrectionPrompt(JsonElement? schema)
    {
        var problems = string.Join(
            Environment.NewLine,
            Problems.Select(problem => $"- {problem.Path}: {problem.Message}")
        );
        return $"""
            Your previous response could not be accepted:

            {problems}

            {AgentStructuredOutputPrompt.Schema(schema)}

            {(
                schema is null
                    ? "Reply with only the corrected response in the requested format."
                    : "Reply with only the corrected JSON object."
            )}
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
