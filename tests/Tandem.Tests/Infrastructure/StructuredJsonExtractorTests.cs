using System.Text.Json;
using FluentAssertions;

namespace Tandem.Tests.Infrastructure;

public sealed class StructuredJsonExtractorTests
{
    [Theory]
    [InlineData("""{"a":1}""", """{"a":1}""")]
    [InlineData("""Here you go: {"a":"}{"} and more {"b":2}""", """{"a":"}{"}""")]
    [InlineData("```json\n{\"a\":{\"b\":[1,2]}}\n```", """{"a":{"b":[1,2]}}""")]
    public void Extract_ReturnsTheFirstJsonObjectAndIgnoresTrailingText(
        string response,
        string expected
    )
    {
        var extracted = AgentStructuredJsonExtractor.Extract(response);

        JsonElement
            .DeepEquals(extracted, JsonDocument.Parse(expected).RootElement)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void Extract_RejectsResponsesWithoutAnObject()
    {
        var act = () => AgentStructuredJsonExtractor.Extract("no json here");

        act.Should().Throw<InvalidOperationException>().WithMessage("*no JSON object*");
    }

    [Theory]
    [InlineData("""{"a":1""")]
    [InlineData("""{a:1}""")]
    public void Extract_RejectsIncompleteOrInvalidObjects(string response)
    {
        var act = () => AgentStructuredJsonExtractor.Extract(response);

        act.Should().Throw<JsonException>();
    }
}
