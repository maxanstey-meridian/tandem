using System.Text.Json;
using Examples.Songwriter;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Domain;

namespace Tandem.Tests.Composition;

public sealed class SongwriterCompositionTests
{
    [Fact]
    public async Task Songwriter_ExecutesBothFeedbackLoopsAndCompletesInProcess()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddSongwriter(
            new SongwriterClients(
                Scripted(
                    order,
                    "songwriter",
                    "{\"lyrics\":\"First draft\"}",
                    "{\"lyrics\":\"Linted\\ndraft\"}",
                    "{\"lyrics\":\"Final\\ndraft\"}"
                ),
                Scripted(
                    order,
                    "proofreader",
                    "{\"accepted\":false,\"feedback\":\"Sharpen the ending.\"}",
                    "{\"accepted\":true,\"feedback\":\"Accepted.\"}"
                )
            )
        );
        await using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<SongwriterComposition>().Build();
        var input = new PipelineMessage<SongwriterState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new SongwriterState("An optimistic song about rebuilding after a storm.")
        );

        var output = await RunAsync(pipeline, input);

        order
            .Should()
            .Equal("songwriter", "songwriter", "proofreader", "songwriter", "proofreader");
        output.State.Lyrics.Should().Be("Final\ndraft");
        output.State.Revision.Should().Be(3);
        output.State.LintFeedback.Should().BeNull();
        output.State.ProofreaderFeedback.Should().Be("Accepted.");
        output.LatestOutcome!.StepId.Should().Be("complete");
        output.LatestOutcome.Kind.Should().Be(StandardOutcomeKinds.Success);
    }

    [Fact]
    public async Task InvalidProofreaderResponse_EndsFailedWithoutRequestingChanges()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddSongwriter(
            new SongwriterClients(
                Scripted(order, "songwriter", "{\"lyrics\":\"Valid\\ndraft\"}"),
                Scripted(order, "proofreader", ["not json", "still not json"])
            )
        );
        await using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<SongwriterComposition>().Build();
        var input = new PipelineMessage<SongwriterState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new SongwriterState("A brief")
        );

        var output = await RunAsync(pipeline, input);

        order.Should().HaveCount(3);
        order[0].Should().Be("songwriter");
        order.Skip(1).Should().OnlyContain(step => step == "proofreader");
        output.Status.Should().Be(PipelineRunStatus.Failed);
        output.LatestOutcome!.Kind.Should().Be(StandardOutcomeKinds.Failed);
        output.LatestResult!.CaseId.Should().Be("Failed");
        output
            .LatestResult.Payload.Deserialize<FailureEvidence>()!
            .Detail.Should()
            .Contain("still not json");
    }

    private static async Task<PipelineMessage<SongwriterState>> RunAsync(
        Pipeline<SongwriterState> pipeline,
        PipelineMessage<SongwriterState> input
    )
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            PipelineMafBridge.GetWorkflow(pipeline),
            input,
            input.Runtime.RunId.ToString("N"),
            CancellationToken.None
        );
        PipelineMessage<SongwriterState>? output = null;
        await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
        {
            if (
                evt is WorkflowOutputEvent workflowOutput
                && workflowOutput.Is<PipelineMessage<SongwriterState>>()
            )
            {
                output = workflowOutput.As<PipelineMessage<SongwriterState>>();
            }
            else if (evt is WorkflowErrorEvent error)
            {
                throw error.Exception
                    ?? new InvalidOperationException("Songwriter workflow failed.");
            }
            else if (evt is ExecutorFailedEvent failed)
            {
                throw failed.Data ?? new InvalidOperationException("Songwriter executor failed.");
            }
        }
        return output ?? throw new InvalidOperationException("Songwriter produced no output.");
    }

    private static TestChatClient Scripted(
        List<string> order,
        string name,
        params string[] responses
    ) => new([.. responses.Select(TestChatClient.Text)]) { OnRequest = () => order.Add(name) };
}
