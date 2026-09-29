using FluentValidation;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class InstructionlessRawOutputTests
{
    [Fact]
    public async Task Raw_agent_can_send_only_the_authored_user_message()
    {
        using var client = TestChatClient.Replying("accepted");
        var agent = Agent
            .Create<string>("raw", "", client)
            .WithMessage(state => state)
            .WithRawOutput(new RawText(), (_, value) => value)
            .Build();
        var result = await new PipelineRunner().RunAsync(
            Pipeline.Start(agent, "user-only").Build(agent),
            "Mr. Burns won by 0.2 seconds."
        );
        Assert.Equal("accepted", result.State);
        var message = Assert.Single(Assert.Single(client.Requests));
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("Mr. Burns won by 0.2 seconds.", message.Text);
        Assert.True(string.IsNullOrWhiteSpace(client.Options[0]?.Instructions));
        Assert.Null(client.Options[0]?.ResponseFormat);
    }

    [Fact]
    public void Normal_agent_still_requires_instructions_when_built()
    {
        using var client = new TestChatClient();
        Assert.Throws<ArgumentException>(() =>
            Agent.Create<string>("normal", "", client).WithMessage(state => state).Build()
        );
        Assert.Throws<ArgumentNullException>(() => Agent.Create<string>("normal", null!, client));
    }

    private sealed class RawText : IAgentRawOutputDefinition<string, string>
    {
        public string Instructions => "";
        public IValidator<string> Validator { get; } = new InlineValidator<string>();

        public string Parse(string response) => response;
    }
}
