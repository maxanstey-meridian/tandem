using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class RegisteredObservationObserverTests
{
    public static TheoryData<PipelineObservation, string> SupportedObservations =>
        new()
        {
            {
                new PipelineStepStarted(Guid.Empty, "work"),
                """{"version":1,"kind":"stepStarted","stepId":"work"}"""
            },
            {
                new PipelineStepCompleted(
                    Guid.Empty,
                    "work",
                    new PipelineRunOutcome("step.completed", "work", "Completed", default, default)
                ),
                """{"version":1,"kind":"stepCompleted","stepId":"work"}"""
            },
            {
                new PipelineStepCancelled(Guid.Empty, "work"),
                """{"version":1,"kind":"stepCancelled","stepId":"work"}"""
            },
            {
                new PipelineStepFaulted(Guid.Empty, "work", "failed"),
                """{"version":1,"kind":"stepFaulted","stepId":"work","error":"failed"}"""
            },
            {
                new PipelineAgentUpdated(Guid.Empty, "agent", new AgentUpdate.Text("answer")),
                """{"version":1,"kind":"agentText","stepId":"agent","text":"answer"}"""
            },
            {
                new PipelineAgentUpdated(
                    Guid.Empty,
                    "agent",
                    new AgentUpdate.Reasoning("thinking")
                ),
                """{"version":1,"kind":"agentReasoning","stepId":"agent","text":"thinking"}"""
            },
            {
                new PipelineAgentUpdated(
                    Guid.Empty,
                    "agent",
                    new AgentUpdate.ModelSelected("actual-model")
                ),
                """{"version":1,"kind":"agentModelSelected","stepId":"agent","modelId":"actual-model"}"""
            },
            {
                new PipelineAgentUsage(Guid.Empty, "agent", 10, 5, 15),
                """{"version":1,"kind":"agentUsage","stepId":"agent","inputTokens":10,"outputTokens":5,"currentContextTokens":15}"""
            },
        };

    [Theory]
    [MemberData(nameof(SupportedObservations))]
    public void ProjectsSupportedObservations(PipelineObservation observation, string expected)
    {
        var projected = RegisteredObservationObserver.Project(observation);

        Assert.Equal(expected, JsonSerializer.Serialize(projected, JsonSerializerOptions.Web));
    }

    [Fact]
    public void IgnoresProviderUsageUpdatesAndAcceptanceObservations()
    {
        Assert.Null(
            RegisteredObservationObserver.Project(
                new PipelineAgentUpdated(Guid.Empty, "agent", new AgentUpdate.Usage(1, 2, 3))
            )
        );
        Assert.Null(
            RegisteredObservationObserver.Project(
                new PipelineStructuredOutputAccepted(Guid.Empty, "agent", "output", "success")
            )
        );
    }
}
