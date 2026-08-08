using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

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
            .Route(on: increment, to: complete, label: "incremented")
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
        output.LatestResult.CaseId.Should().Be("Success");
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

    [Fact]
    public async Task UnconditionalRoute_AcceptsCustomResultAndStateOnlyStepsHaveNoSelectors()
    {
        var increment = new IncrementStage();
        var complete = new StateCompleteStage();
        var pipeline = TandemWorkflow
            .Start(at: increment, name: "unconditional-authoring")
            .Route(on: increment, to: complete, label: "continue")
            .Build(complete);

        var output = await RunAsync(
            pipeline.Workflow,
            new PipelineMessage<CounterState>(
                PipelineRuntime.Create(Guid.CreateVersion7()),
                new CounterState(0)
            )
        );

        output.State.Count.Should().Be(2);
        typeof(StateCompleteStage).GetProperty("Success").Should().BeNull();
        output.LatestOutcome!.Payload.ValueKind.Should().Be(JsonValueKind.Object);
        var json = () => JsonSerializer.Serialize(output);
        json.Should().NotThrow();
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 11)]
    public async Task StandardOutcome_UsesTypedSuccessAndFailedRoutes(bool fail, int expected)
    {
        var outcome = new StandardOutcomeStage(fail);
        var success = new StateCompleteStage();
        var recovery = new RecoveryStage();
        var pipeline = TandemWorkflow
            .Start(at: outcome, name: "standard-outcome")
            .Route(on: outcome.Success, to: success, label: "success")
            .Route(on: outcome.Failed, to: recovery, label: "recover")
            .Build(success, recovery);

        var output = await RunAsync(
            pipeline.Workflow,
            new PipelineMessage<CounterState>(
                PipelineRuntime.Create(Guid.CreateVersion7()),
                new CounterState(0)
            )
        );

        output.State.Count.Should().Be(expected);
        output.Disposition.Should().BeNull();
    }

    [Fact]
    public async Task StandardFailed_PreservesTypedFailureEvidenceWhenItIsAnOutput()
    {
        var outcome = new StandardOutcomeStage(fail: true);
        var pipeline = TandemWorkflow
            .Start(at: outcome, name: "standard-failure-evidence")
            .Build(outcome);

        var output = await RunAsync(
            pipeline.Workflow,
            new PipelineMessage<CounterState>(
                PipelineRuntime.Create(Guid.CreateVersion7()),
                new CounterState(0)
            )
        );

        output.LatestOutcome!.Kind.Should().Be(StandardOutcomeKinds.Failed);
        output
            .LatestOutcome.Payload.Deserialize<FailureEvidence>()
            .Should()
            .Be(new FailureEvidence("test.failure", "Expected failure"));
        output.Disposition.Should().Be(PipelineRunDisposition.Failed);
    }

    [Theory]
    [InlineData(1, true, 11)]
    [InlineData(1, false, 1)]
    public async Task ConditionalFailedRoute_SuppressesDispositionOnlyWhenStateMatches(
        int failedCount,
        bool matches,
        int expectedCount
    )
    {
        var outcome = new StandardOutcomeStage(fail: true);
        var recovery = new RecoveryStage();
        var pipeline = TandemWorkflow
            .Start(at: outcome, name: "conditional-standard-failure")
            .Route(
                on: outcome.Failed,
                when: state => matches && state.Count == failedCount,
                to: recovery,
                label: "conditional recovery"
            )
            .Build(outcome, recovery);

        var output = await RunAsync(
            pipeline.Workflow,
            new PipelineMessage<CounterState>(
                PipelineRuntime.Create(Guid.CreateVersion7()),
                new CounterState(0)
            )
        );

        output.State.Count.Should().Be(expectedCount);
        output.Disposition.Should().Be(matches ? null : PipelineRunDisposition.Failed);
        var roundTrip = JsonSerializer.Deserialize<PipelineMessage<CounterState>>(
            JsonSerializer.Serialize(output)
        );
        roundTrip!.Disposition.Should().Be(output.Disposition);
    }

    [Fact]
    public async Task UnconditionalOutputRoute_HandlesStandardFailure()
    {
        var outcome = new StandardOutcomeStage(fail: true);
        var recovery = new RecoveryStage();
        var pipeline = TandemWorkflow
            .Start(at: outcome, name: "unconditional-standard-failure")
            .Route(on: outcome, to: recovery, label: "recover")
            .Build(recovery);

        var output = await RunAsync(
            pipeline.Workflow,
            new PipelineMessage<CounterState>(
                PipelineRuntime.Create(Guid.CreateVersion7()),
                new CounterState(0)
            )
        );

        output.State.Count.Should().Be(11);
        output.Disposition.Should().BeNull();
    }

    [Fact]
    public void Route_RejectsMixedOutgoingModesForSameSource()
    {
        var outcome = new StandardOutcomeStage(false);
        var complete = new StateCompleteStage();
        var builder = TandemWorkflow
            .Start(at: outcome, name: "invalid-routes")
            .Route(on: outcome, to: complete, label: "all");

        var act = () => builder.Route(on: outcome.Success, to: complete, label: "success");

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*cannot mix unconditional and outcome-specific*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedOutcomeDescriptor_KeepsFailureAwarenessBoundToEachPipeline(
        bool buildRecoveryLast
    )
    {
        var outcome = new ReusableOutcomeStep();
        var recovery = new RecoveryStage();
        var recoveryBuilder = TandemWorkflow
            .Start(outcome, "reused-outcome-recovery")
            .Route(outcome.Failed, recovery, "recover");
        var terminalBuilder = TandemWorkflow.Start(outcome, "reused-outcome-terminal");

        var recoveryPipeline = buildRecoveryLast ? null : recoveryBuilder.Build(recovery);
        var terminalPipeline = terminalBuilder.Build(outcome);
        recoveryPipeline ??= recoveryBuilder.Build(recovery);

        var outputs = await Task.WhenAll(
            RunAsync(
                recoveryPipeline.Workflow,
                new PipelineMessage<CounterState>(
                    PipelineRuntime.Create(Guid.CreateVersion7()),
                    new CounterState(0)
                )
            ),
            RunAsync(
                terminalPipeline.Workflow,
                new PipelineMessage<CounterState>(
                    PipelineRuntime.Create(Guid.CreateVersion7()),
                    new CounterState(0)
                )
            )
        );

        outputs[0].State.Count.Should().Be(10);
        outputs[0].Disposition.Should().BeNull();
        outputs[1].Disposition.Should().Be(PipelineRunDisposition.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedAgentDefinition_KeepsFailureAwarenessBoundToEachPipeline(
        bool buildRecoveryLast
    )
    {
        var definition = new AgentDefinition<CounterState>(
            "reused-agent",
            new AgentOperation<CounterState>(
                (pipeline, _) =>
                    ValueTask.FromResult(
                        pipeline with
                        {
                            LatestOutcome = new BlockOutcome(
                                "agent.failed",
                                "reused-agent",
                                "Expected failure"
                            ),
                        }
                    )
            )
        );
        var recovery = new RecoveryStage();
        var recoveryBuilder = TandemWorkflow
            .Start(definition, "reused-agent-recovery")
            .Route(definition.Failed, recovery, "recover");
        var terminalBuilder = TandemWorkflow.Start(definition, "reused-agent-terminal");

        var recoveryPipeline = buildRecoveryLast ? null : recoveryBuilder.Build(recovery);
        var terminalPipeline = terminalBuilder.Build(definition);
        recoveryPipeline ??= recoveryBuilder.Build(recovery);

        var outputs = await Task.WhenAll(
            RunAsync(
                recoveryPipeline.Workflow,
                new PipelineMessage<CounterState>(
                    PipelineRuntime.Create(Guid.CreateVersion7()),
                    new CounterState(0)
                )
            ),
            RunAsync(
                terminalPipeline.Workflow,
                new PipelineMessage<CounterState>(
                    PipelineRuntime.Create(Guid.CreateVersion7()),
                    new CounterState(0)
                )
            )
        );

        outputs[0].State.Count.Should().Be(10);
        outputs[0].Disposition.Should().BeNull();
        outputs[1].Disposition.Should().Be(PipelineRunDisposition.Failed);
    }

    [Fact]
    public void Build_RejectsReusingOneMutableBuilder()
    {
        var increment = new IncrementStage();
        var builder = TandemWorkflow.Start(increment, "single-use-builder");
        builder.Build(increment);

        var act = () => builder.Build(increment);

        act.Should().Throw<InvalidOperationException>().WithMessage("*build only once*");
    }

    [Fact]
    public void Build_RejectsAddingRoutesAfterPipelineIsBuilt()
    {
        var increment = new IncrementStage();
        var target = new IncrementStage();
        var builder = TandemWorkflow.Start(increment, "immutable-built-pipeline");
        builder.Build(increment);

        var act = () => builder.Route(on: increment, to: target, label: "too late");

        act.Should().Throw<InvalidOperationException>().WithMessage("*cannot be modified*");
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

public sealed record CounterState(int Count);

[PipelineStage("increment")]
public sealed partial class IncrementStage
{
    public ValueTask<CounterState> ExecuteAsync(CounterState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Count = state.Count + 1 });
}

[PipelineStage("complete")]
public sealed partial class CompleteStage
{
    public ValueTask<CounterState> ExecuteAsync(CounterState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Count = state.Count + 1 });
}

[PipelineStage("state-complete")]
public sealed partial class StateCompleteStage
{
    public ValueTask<CounterState> ExecuteAsync(CounterState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Count = state.Count + 1 });
}

[PipelineStage("standard-outcome")]
public sealed partial class StandardOutcomeStage(bool fail)
{
    public ValueTask<Outcome<CounterState>> ExecuteAsync(CounterState state, CancellationToken _) =>
        ValueTask.FromResult<Outcome<CounterState>>(
            fail
                ? new Outcome<CounterState>.Failed(
                    state with
                    {
                        Count = state.Count + 1,
                    },
                    new FailureEvidence("test.failure", "Expected failure")
                )
                : new Outcome<CounterState>.Success(state with { Count = state.Count + 1 })
        );
}

[PipelineStage("recovery")]
public sealed partial class RecoveryStage
{
    public ValueTask<CounterState> ExecuteAsync(CounterState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Count = state.Count + 10 });
}

internal sealed class ReusableOutcomeStep : IStandardOutcomePipelineStep<CounterState>
{
    private readonly GeneratedOutcomeStepDescriptor<CounterState> _descriptor;

    public ReusableOutcomeStep()
    {
        _descriptor = new GeneratedOutcomeStepDescriptor<CounterState>(
            Id,
            (state, _) =>
                ValueTask.FromResult<Outcome<CounterState>>(
                    new Outcome<CounterState>.Failed(
                        state,
                        new FailureEvidence("test.failure", "Expected failure")
                    )
                )
        );
    }

    public string Id => "reusable-outcome";
    public PipelineNodeDescriptor Descriptor => _descriptor;
    public PipelineOutcomeSelector<CounterState> Success => new(this, failed: false);
    public PipelineOutcomeSelector<CounterState> Failed => new(this, failed: true);
}
