using System.Text.Json;
using Dunet;
using FluentAssertions;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;
using Tandem.Tests.Durable;

namespace Tandem.Tests.Composition;

public sealed class GeneratedAuthoringVerticalTests
{
    [Fact]
    public async Task GeneratedResultRoute_ExecutesReflectsAndSerializes()
    {
        var increment = new IncrementStage();
        var complete = new CompleteStage();
        var pipeline = TandemWorkflow
            .Start(at: increment, name: "generated-authoring")
            .Route(on: increment.Result.Incremented, to: complete, label: "incremented")
            .Build(complete);
        var input = new PipelineMessage<CounterState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new CounterState(0)
        );

        var output = await RunAsync(pipeline.Workflow, input);

        output.State.Count.Should().Be(2);
        output.Runtime.Should().BeSameAs(input.Runtime);
        output.LatestResult.Should().NotBeNull();
        output.LatestResult!.StepId.Should().Be("complete");
        output.LatestResult.CaseId.Should().Be("Completed");
        pipeline.Workflow.ReflectExecutors().Keys.Should().BeEquivalentTo("increment", "complete");
        pipeline.Workflow.ReflectEdges()["increment"].Should().ContainSingle();

        var json = JsonSerializer.Serialize(output);
        var roundTrip = JsonSerializer.Deserialize<PipelineMessage<CounterState>>(json);
        roundTrip.Should().NotBeNull();
        roundTrip!.Runtime.Should().BeEquivalentTo(output.Runtime);
        roundTrip.State.Should().Be(output.State);
        roundTrip.LatestResult!.StepId.Should().Be(output.LatestResult.StepId);
        roundTrip.LatestResult.CaseId.Should().Be(output.LatestResult.CaseId);
        JsonElement
            .DeepEquals(roundTrip.LatestResult.Payload, output.LatestResult.Payload)
            .Should()
            .BeTrue();
    }

    private static async Task<PipelineMessage<CounterState>> RunAsync(
        Workflow workflow,
        PipelineMessage<CounterState> input
    )
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            input,
            "generated-authoring-" + Guid.NewGuid().ToString("N"),
            CancellationToken.None
        );
        PipelineMessage<CounterState>? output = null;
        Exception? failure = null;

        await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowErrorEvent error)
            {
                failure = error.Exception;
            }
            else if (evt is ExecutorFailedEvent failed)
            {
                failure = failed.Data;
            }
            else if (
                evt is WorkflowOutputEvent workflowOutput
                && workflowOutput.Is<PipelineMessage<CounterState>>()
            )
            {
                output = workflowOutput.As<PipelineMessage<CounterState>>();
            }
        }

        failure.Should().BeNull();
        output.Should().NotBeNull();
        return output!;
    }
}

[Collection("Durable Task Scheduler")]
public sealed class GeneratedAuthoringDurableTests
{
    [Fact]
    public async Task GeneratedResultRoute_ExecutesDurablyAsClosedGenericMessage()
    {
        DtsFixture.EnsureReachable();
        var increment = new IncrementStage();
        var complete = new CompleteStage();
        var pipeline = TandemWorkflow
            .Start(at: increment, name: "generated-authoring-durable")
            .Route(on: increment.Result.Incremented, to: complete, label: "incremented")
            .Build(complete);
        var input = new PipelineMessage<CounterState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new CounterState(0)
        );

        await using var host = await DurableHost.StartAsync(options =>
            options.AddWorkflow(pipeline.Workflow)
        );
        var run = (IAwaitableWorkflowRun)
            await host.WorkflowClient.RunAsync(
                pipeline.Workflow,
                input,
                "generated-authoring-durable-" + Guid.NewGuid().ToString("N")
            );
        var output = await run.WaitForCompletionAsync<PipelineMessage<CounterState>>();

        output.Should().NotBeNull();
        output!.State.Count.Should().Be(2);
        output
            .LatestResult.Should()
            .Be(new PipelineResult("complete", "Completed", output.LatestResult!.Payload));
    }
}

public sealed record CounterState(int Count);

[PipelineStage("increment")]
public sealed partial class IncrementStage
{
    [Union(EnableImplicitConversions = false)]
    public partial record IncrementResult
    {
        public partial record Incremented(CounterState State);
    }

    public ValueTask<IncrementResult> ExecuteAsync(
        PipelineMessage<CounterState> pipeline,
        CancellationToken _
    ) =>
        ValueTask.FromResult<IncrementResult>(
            new IncrementResult.Incremented(
                pipeline.State with
                {
                    Count = pipeline.State.Count + 1,
                }
            )
        );
}

[PipelineStage("complete")]
public sealed partial class CompleteStage
{
    [Union(EnableImplicitConversions = false)]
    public partial record CompleteResult
    {
        public partial record Completed(CounterState State);
    }

    public ValueTask<CompleteResult> ExecuteAsync(
        PipelineMessage<CounterState> pipeline,
        CancellationToken _
    ) =>
        ValueTask.FromResult<CompleteResult>(
            new CompleteResult.Completed(pipeline.State with { Count = pipeline.State.Count + 1 })
        );
}
