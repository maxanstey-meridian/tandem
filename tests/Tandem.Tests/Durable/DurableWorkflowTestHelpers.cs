using FluentAssertions;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;

namespace Tandem.Tests.Durable;

internal static class DurableWorkflowTestHelpers
{
    public static async Task<IReadOnlyList<WorkflowEvent>> WatchToCompletionAsync(
        IStreamingWorkflowRun run,
        CancellationToken cancellationToken = default
    )
    {
        var events = new List<WorkflowEvent>();

        await foreach (var evt in run.WatchStreamAsync(cancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    public static void AssertCompleted(IReadOnlyList<WorkflowEvent> events)
    {
        events.OfType<DurableWorkflowFailedEvent>().Should().BeEmpty();
        events.OfType<DurableWorkflowCompletedEvent>().Should().ContainSingle();
    }
}
