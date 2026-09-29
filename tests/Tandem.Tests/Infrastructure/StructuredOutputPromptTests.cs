using System.Text.Json;
using FluentAssertions;
using Tandem.Domain;

namespace Tandem.Tests.Infrastructure;

public sealed class StructuredOutputPromptTests
{
    private static readonly JsonElement _schema = JsonSerializer.SerializeToElement(
        new
        {
            type = "object",
            properties = new
            {
                selected_source_indexes = new
                {
                    type = "array",
                    items = new { type = "integer" },
                    maxItems = 15,
                },
            },
            required = new[] { "selected_source_indexes" },
            additionalProperties = false,
        }
    );

    [Fact]
    public void InitialInstructions_IncludeAuthoredInstructionsAndExactSchema()
    {
        var output = new AgentStructuredOutputDescriptor<int, int>(
            (_, _) => throw new NotSupportedException(),
            (state, _) => state,
            "test",
            "Return only ordered distinct existing source indexes.",
            _schema
        );

        var prompt = AgentStructuredOutputPrompt.Initial(output);

        prompt.Should().Contain("Return only ordered distinct existing source indexes.");
        prompt.Should().Contain("Return a JSON object matching this schema:");
        prompt.Should().Contain(_schema.GetRawText());
    }

    [Fact]
    public void CorrectionPrompt_RepeatsProblemsAndExactSchema()
    {
        var result = AgentStructuredOutputResult<int>.Rejected(
            [new ValidationProblem("$.selected_source_indexes", "Field is required.")],
            "{}"
        );

        var prompt = result.CorrectionPrompt(_schema);

        prompt.Should().Contain("$.selected_source_indexes: Field is required.");
        prompt.Should().Contain("Return a JSON object matching this schema:");
        prompt.Should().Contain(_schema.GetRawText());
        prompt.Should().EndWith("Reply with only the corrected JSON object.");
    }
}
