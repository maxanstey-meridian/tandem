using FluentAssertions;
using Tandem.Tool;

namespace Tandem.Tests.Infrastructure;

public sealed class TerminalHumanInteractionTests
{
    [Fact]
    public async Task SubmittedAnswer_CompletesPendingInteraction()
    {
        var runId = Guid.CreateVersion7();
        var interaction = new TerminalHumanInteraction();
        var context = new PipelineInteractionContext<HumanQuestion, HumanAnswer>(
            runId,
            "request-1",
            "PlannerHumanInput",
            new HumanQuestion("Which behavior?", "A decision is required.")
        );
        var waiting = interaction.WaitAsync(context, CancellationToken.None).AsTask();

        await interaction.SubmitAsync(runId, "Accepted answer");

        (await waiting).Text.Should().Be("Accepted answer");
    }
}
