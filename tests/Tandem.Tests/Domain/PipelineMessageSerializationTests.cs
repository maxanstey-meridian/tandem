using System.Text.Json;
using FluentAssertions;
using Tandem.Domain;

namespace Tandem.Tests.Domain;

public sealed class PipelineMessageSerializationTests
{
    [Fact]
    public void ExecutorTransitions_RoundTripWithTheirTypedPayloads()
    {
        ExecutorTransition[] facts =
        [
            new ExecutorTransition.PlannerRequested(
                new AskPlannerRequest("question", "approach", ["evidence"])
            ),
            new ExecutorTransition.ReportSubmitted(
                new SubmitReportRequest("summary", ["outcome"], ["evidence"])
            ),
            new ExecutorTransition.CheckpointWritten(
                new WriteCheckpointRequest(
                    "summary",
                    ["completed"],
                    ["README.md"],
                    ["uncertain"],
                    "next"
                )
            ),
        ];

        foreach (var fact in facts)
        {
            var json = JsonSerializer.Serialize<ExecutorTransition>(fact);
            var roundTrip = JsonSerializer.Deserialize<ExecutorTransition>(json);

            roundTrip.Should().NotBeNull().And.BeOfType(fact.GetType());
            JsonElement
                .DeepEquals(
                    JsonSerializer.SerializeToElement(fact, fact.GetType()),
                    JsonSerializer.SerializeToElement(roundTrip, roundTrip!.GetType())
                )
                .Should()
                .BeTrue();
        }
    }

    [Fact]
    public void FullyPopulatedDeliveryMessage_RoundTripsAsClosedGenericType()
    {
        var runId = Guid.CreateVersion7();
        var plannerDecision = new PlannerDecision(
            PlannerDecisionValue.ProceedWithConstraints,
            "Proceed carefully.",
            ["Keep the public contract."],
            ["src/service.ts"],
            null
        );
        var runtime = PipelineRuntime
            .Create(runId)
            .WithSession("executor", JsonSerializer.SerializeToElement(new { stored = true }))
            .WithUsage("executor", new AgentUsage(10, 5, 15, 1000, 800, TimeSpan.FromSeconds(1)))
            .IncrementInvocations("executor");
        var packet = new Packet(
            "Serialize",
            "/repo",
            "main",
            [new Outcome("one", "Deliver one.")],
            ["task test"],
            ["No dependencies."],
            "Context"
        );
        var state = DeliveryState.Create(packet, "base-sha", "/workspace") with
        {
            MutationAuthorized = true,
            PlannerDecision = plannerDecision,
            PlannerConstraints = plannerDecision.Constraints,
            CandidateSha = "candidate-sha",
            VerificationIndex = 1,
            VerificationResults =
            [
                new VerificationResult(0, "task test", 0, "ok", "", TimeSpan.FromSeconds(2), false),
            ],
            ExecutorTransition = new ExecutorTransition.ReportSubmitted(
                new SubmitReportRequest("done", ["outcome-1"], ["evidence-1"])
            ),
        };
        var message = new PipelineMessage<DeliveryState>(
            runtime,
            state,
            new BlockOutcome(
                StandardOutcomeKinds.Success,
                DeliveryIds.Complete,
                "ready",
                JsonSerializer.SerializeToElement(new { candidate = "candidate-sha" }),
                TimeSpan.FromSeconds(3)
            ),
            Status: PipelineRunStatus.Failed
        );

        var json = JsonSerializer.Serialize(message);
        var roundTrip = JsonSerializer.Deserialize<PipelineMessage<DeliveryState>>(json);

        roundTrip.Should().NotBeNull();
        roundTrip!.Runtime.RunId.Should().Be(runId);
        roundTrip.Runtime.AgentSessions.Should().ContainKey("executor");
        roundTrip.Runtime.AgentUsage["executor"].CurrentContextTokens.Should().Be(15);
        roundTrip.Runtime.InvocationCounts["executor"].Should().Be(1);
        roundTrip.State.Should().BeEquivalentTo(state);
        roundTrip.LatestOutcome!.Kind.Should().Be(message.LatestOutcome!.Kind);
        roundTrip.Status.Should().Be(PipelineRunStatus.Failed);
        roundTrip.LatestOutcome.BlockId.Should().Be(message.LatestOutcome.BlockId);
        roundTrip.LatestOutcome.Summary.Should().Be(message.LatestOutcome.Summary);
        JsonElement
            .DeepEquals(roundTrip.LatestOutcome.Payload, message.LatestOutcome.Payload)
            .Should()
            .BeTrue();
    }
}
