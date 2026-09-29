using System.Text.Json;
using FluentAssertions;

namespace Tandem.Tests.Infrastructure;

public sealed class StructuredOutputSchemaTests
{
    [Fact]
    public void Schema_PropertyNames_MatchTandemJsonContract()
    {
        var schema = StructuredOutputSchema.CreateJsonSchema<AcronymDecision>();
        var serialized = JsonSerializer.SerializeToElement(
            new AcronymDecision("https://example.test", 7, [new ReviewNote("n", Severity.High)]),
            TandemJson.TypedContract
        );

        PropertyNames(schema).Should().Equal(serialized.EnumerateObject().Select(p => p.Name));
        var note = schema.GetProperty("properties").GetProperty("notes").GetProperty("items");
        PropertyNames(note)
            .Should()
            .Equal(serialized.GetProperty("notes")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Schema_IsStrictAndRequiresEveryProperty()
    {
        var schema = StructuredOutputSchema.CreateJsonSchema<AcronymDecision>();

        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .BeEquivalentTo(PropertyNames(schema));
    }

    private static IEnumerable<string> PropertyNames(JsonElement schema) =>
        schema.GetProperty("properties").EnumerateObject().Select(property => property.Name);

    private sealed record AcronymDecision(
        string URL,
        int HTTPStatus,
        IReadOnlyList<ReviewNote> Notes
    );

    private sealed record ReviewNote(string ID, Severity Severity);

    private enum Severity
    {
        Low,
        High,
    }
}
