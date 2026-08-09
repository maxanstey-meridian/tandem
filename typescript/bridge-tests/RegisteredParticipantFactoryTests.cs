using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class RegisteredParticipantFactoryTests
{
    [Fact]
    public void ParseValidationProblems_PreservesJsonPath()
    {
        var problems = RegisteredParticipantFactory.ParseValidationProblems(
            "[{\"path\":\"$.answer\",\"message\":\"Required\"}]"
        );

        var problem = Assert.Single(problems);
        Assert.Equal("$.answer", problem.Field);
        Assert.Equal("Required", problem.Message);
    }

    [Fact]
    public void ParseValidationProblems_MalformedCallbackJson_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            RegisteredParticipantFactory.ParseValidationProblems("not-json")
        );
    }
}
