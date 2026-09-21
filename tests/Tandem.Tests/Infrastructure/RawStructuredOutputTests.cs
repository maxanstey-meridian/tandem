using System.Text.Json;
using FluentAssertions;
using FluentValidation;

namespace Tandem.Tests.Infrastructure;

public sealed class RawStructuredOutputTests
{
    [Fact]
    public void ParseRaw_ParsesFreeTextAndValidatesParsedValue()
    {
        var definition = new TestRawOutputDefinition(response =>
            JsonSerializer.SerializeToElement(new { items = new[] { response.Trim() } })
        );
        definition
            .ValidatorInternal.RuleFor(o => o.GetProperty("items").GetArrayLength())
            .GreaterThan(0);

        var result = AgentStructuredOutputPolicy.ParseRaw<JsonElement, int>(
            "PROPOSITIONS:\n\n- One proposition.",
            definition.Parse,
            definition.Validator
        );

        result.Success.Should().BeTrue();
        result.Outcome!.Payload.Should().NotBeNull();
    }

    [Fact]
    public void ParseRaw_FailsOnUnparseableResponse()
    {
        var definition = new TestRawOutputDefinition(_ =>
            throw new InvalidOperationException("No PROPOSITIONS section found.")
        );

        var result = AgentStructuredOutputPolicy.ParseRaw<JsonElement, int>(
            "",
            definition.Parse,
            definition.Validator
        );

        result.Success.Should().BeFalse();
        result.Problems.Should().ContainSingle().Which.Message.Should().Contain("PROPOSITIONS");
    }

    [Fact]
    public void ParseRaw_ReportsValidatorProblemsWithCamelCasePaths()
    {
        var definition = new TestRawOutputDefinition(_ =>
            JsonSerializer.SerializeToElement(new { items = Array.Empty<string>() })
        );
        definition
            .ValidatorInternal.RuleFor(o => o.GetProperty("items").GetArrayLength())
            .GreaterThan(0)
            .WithName("items");

        var result = AgentStructuredOutputPolicy.ParseRaw<JsonElement, int>(
            "",
            definition.Parse,
            definition.Validator
        );

        result.Success.Should().BeFalse();
        result.Problems.Should().ContainSingle();
    }

    private sealed class TestRawOutputDefinition(Func<string, JsonElement> parse)
        : IAgentRawOutputDefinition<int, JsonElement>
    {
        public string Instructions => "Return propositions.";

        public JsonElement Parse(string response) => parse(response);

        public IValidator<JsonElement> Validator => ValidatorInternal;

        public InlineValidator<JsonElement> ValidatorInternal { get; } = new();
    }
}
