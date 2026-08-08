using FluentAssertions;
using Tandem.Tool;

namespace Tandem.Tests.Infrastructure;

public sealed class TerminalHumanInteractionTests
{
    [Fact]
    public async Task PersistenceFailure_LeavesInteractionPendingForRetry()
    {
        var runId = Guid.CreateVersion7();
        var records = new FakeDeliveryRecordSink { FailHumanAnswers = true };
        var interaction = new TerminalHumanInteraction(records);
        var context = new PipelineInteractionContext<HumanQuestion, HumanAnswer>(
            runId,
            "request-1",
            "PlannerHumanInput",
            new HumanQuestion("Which behavior?", "A decision is required.")
        );
        var waiting = interaction.WaitAsync(context, CancellationToken.None).AsTask();

        var failed = async () => await interaction.SubmitAsync(runId, "First answer");

        await failed.Should().ThrowAsync<IOException>();
        waiting.IsCompleted.Should().BeFalse();

        records.FailHumanAnswers = false;
        await interaction.SubmitAsync(runId, "Accepted answer");

        (await waiting).Text.Should().Be("Accepted answer");
        records
            .HumanAnswerAttempts.Select(attempt => attempt.RequestId)
            .Should()
            .Equal("request-1", "request-1");
    }
}
