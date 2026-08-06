namespace Tandem.Domain;

public sealed record StructuredOutputProblem(string Field, string Message);

public sealed record StructuredOutputResult<TState>(
    StructuredOutcome<TState>? Outcome,
    IReadOnlyList<StructuredOutputProblem> Problems,
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
