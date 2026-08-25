using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class RegisteredParticipantFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateCheckpointPolicy_PreservesDisableCompaction(bool disableCompaction)
    {
        var dispatcher = new CallbackDispatcher(
            new SynchronizationContext(),
            (_, _, _) => "{\"succeeded\":true,\"value\":\"checkpoint\"}",
            (_, _, _, _) => Task.FromResult("{\"succeeded\":true,\"value\":\"checkpoint\"}"),
            CancellationToken.None
        );
        var contract = new RegisteredCheckpointContract(
            100,
            20,
            80,
            "checkpoint",
            "Checkpoint.",
            "checkpoint.message",
            true,
            disableCompaction
        );

        var policy = RegisteredParticipantFactory.CreateCheckpointPolicy(
            contract,
            null!,
            dispatcher
        );

        Assert.Equal(disableCompaction, policy.DisableCompaction);
        Assert.Equal(100, policy.ContextWindowTokens);
        Assert.Equal(20, policy.MaxOutputTokens);
        Assert.Equal(80, policy.CheckpointAtPercent);
        Assert.Equal(Tandem.Advanced.CheckpointSessionBehavior.Reset, policy.SessionBehavior);
    }

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

    [Fact]
    public void ParseCommands_PreservesParameterizedCommandContract()
    {
        var commands = RegisteredParticipantFactory.ParseCommands(
            """
            [{
              "name":"review",
              "description":"Run review.",
              "command":"review",
              "arguments":[
                {"name":"path","description":"Path.","flag":"--path","pattern":"src/.+","allowedValues":null,"maxLength":200},
                {"name":"mode","description":"Mode.","flag":"--mode","pattern":null,"allowedValues":["fast","thorough"],"maxLength":20}
              ]
            }]
            """
        );

        var command = Assert.Single(commands);
        Assert.Collection(
            command.Arguments,
            argument =>
            {
                Assert.Equal("path", argument.Name);
                Assert.Equal("src/.+", argument.Pattern);
                Assert.Null(argument.AllowedValues);
                Assert.Equal(200, argument.MaxLength);
            },
            argument =>
            {
                Assert.Equal("mode", argument.Name);
                Assert.Null(argument.Pattern);
                Assert.Equal(["fast", "thorough"], argument.AllowedValues);
                Assert.Equal(20, argument.MaxLength);
            }
        );
    }
}
