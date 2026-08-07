using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Tests.Composition;

public sealed class ExecutionEnvelopeInvariantTests
{
    [Fact]
    public async Task StateReturn_ReplacesOnlyStateAfterOperationUpdatesRuntime()
    {
        var input = Message(1, "state");
        var updatedRuntime = UpdatedRuntime(input.Runtime, "state-operation");
        var step = new EnvelopeStateStage(
            input with
            {
                Runtime = updatedRuntime,
                LatestOutcome = Outcome("state-operation"),
            }
        );

        var output = await RunAsync(
            TandemWorkflow.Start(step, "state-envelope").Build(step),
            input
        );

        output.State.Should().Be(new EnvelopeState(2, "state"));
        output.Runtime.Should().BeSameAs(updatedRuntime);
        output.Runtime.AgentSessions.Should().ContainKey("state-operation");
    }

    [Fact]
    public async Task OutcomeAdaptation_PreservesOperationEnvelopeUpdates()
    {
        var input = Message(4, "custom");
        var operationOutcome = Outcome("custom-operation");
        var operationMessage = input with
        {
            Runtime = UpdatedRuntime(input.Runtime, "custom-operation"),
            State = input.State with { Count = 5 },
            LatestOutcome = operationOutcome,
        };
        var step = new EnvelopeCustomStage(operationMessage);

        var output = await RunAsync(
            TandemWorkflow.Start(step, "custom-envelope").Build(step),
            input
        );

        output.State.Count.Should().Be(5);
        output.Runtime.Should().BeSameAs(operationMessage.Runtime);
        output.Runtime.InvocationCounts["custom-operation"].Should().Be(1);
        output.LatestOutcome!.Kind.Should().Be(StandardOutcomeKinds.Success);
        output.LatestResult!.CaseId.Should().Be("Success");
    }

    [Fact]
    public async Task NestedGeneratedExecution_RestoresOuterScope()
    {
        var input = Message(1, "outer");
        var outerResult = input with
        {
            Runtime = UpdatedRuntime(input.Runtime, "outer-after-inner"),
            State = input.State with { Count = 3 },
            LatestOutcome = Outcome("outer-after-inner"),
        };
        var step = new NestedEnvelopeStage(outerResult);

        var output = await RunAsync(
            TandemWorkflow.Start(step, "nested-envelope").Build(step),
            input
        );

        output.State.Count.Should().Be(3);
        output.Runtime.AgentSessions.Should().ContainKey("outer-after-inner");
        output.Runtime.AgentSessions.Should().NotContainKey("inner");
    }

    [Fact]
    public async Task ParallelGeneratedExecutions_DoNotCrossContaminateScopes()
    {
        var gate = new EnvelopeGate(2);
        var firstInput = Message(1, "first");
        var secondInput = Message(10, "second");
        var first = new ParallelEnvelopeStage(
            firstInput with
            {
                Runtime = UpdatedRuntime(firstInput.Runtime, "first"),
                State = firstInput.State with { Count = 2 },
                LatestOutcome = Outcome("first"),
            },
            gate
        );
        var second = new ParallelEnvelopeStage(
            secondInput with
            {
                Runtime = UpdatedRuntime(secondInput.Runtime, "second"),
                State = secondInput.State with { Count = 11 },
                LatestOutcome = Outcome("second"),
            },
            gate
        );

        var outputs = await Task.WhenAll(
            RunAsync(TandemWorkflow.Start(first, "parallel-first").Build(first), firstInput),
            RunAsync(TandemWorkflow.Start(second, "parallel-second").Build(second), secondInput)
        );

        outputs[0].Runtime.RunId.Should().Be(firstInput.Runtime.RunId);
        outputs[0].Runtime.AgentSessions.Keys.Should().BeEquivalentTo("first");
        outputs[0].State.Should().Be(new EnvelopeState(2, "first"));
        outputs[1].Runtime.RunId.Should().Be(secondInput.Runtime.RunId);
        outputs[1].Runtime.AgentSessions.Keys.Should().BeEquivalentTo("second");
        outputs[1].State.Should().Be(new EnvelopeState(11, "second"));
    }

    [Fact]
    public async Task ConcurrentOperationsWithinGeneratedExecution_AreRejectedDeterministically()
    {
        var input = Message(1, "siblings");
        var step = new ConcurrentOperationEnvelopeStage(
            input with
            {
                State = input.State with { Count = 2 },
                LatestOutcome = Outcome("siblings"),
            }
        );

        var output = await RunAsync(
            TandemWorkflow.Start(step, "concurrent-operation-envelope").Build(step),
            input
        );

        output.State.Count.Should().Be(3);
        output
            .State.Owner.Should()
            .Be(
                "Concurrent sibling operations cannot run within the same generated pipeline step. Await the active operation before starting another."
            );
    }

    [Fact]
    public async Task FailedAndCancelledOperations_ReleaseConcurrentOperationGuard()
    {
        var input = Message(1, "release");
        var step = new ReleasingOperationEnvelopeStage(
            input with
            {
                State = input.State with { Count = 2 },
                LatestOutcome = Outcome("release"),
            }
        );

        var output = await RunAsync(
            TandemWorkflow.Start(step, "releasing-operation-envelope").Build(step),
            input
        );

        output.State.Should().Be(new EnvelopeState(2, "release"));
    }

    [Fact]
    public async Task Cancellation_ClearsExecutionScope()
    {
        var step = new CancelledEnvelopeStage();

        await RunForFailureAsync(
            TandemWorkflow.Start(step, "cancelled-envelope").Build(step),
            Message(0, "cancelled"),
            CancellationToken.None
        );

        await AssertOperationOutsideExecutionFailsAsync();
    }

    [Fact]
    public async Task Exception_ClearsExecutionScope()
    {
        var step = new FaultedEnvelopeStage();

        await RunForFailureAsync(
            TandemWorkflow.Start(step, "faulted-envelope").Build(step),
            Message(0, "faulted"),
            CancellationToken.None
        );

        await AssertOperationOutsideExecutionFailsAsync();
    }

    [Fact]
    public async Task OperationOutsideGeneratedExecution_FailsClearly()
    {
        await AssertOperationOutsideExecutionFailsAsync();
    }

    private static PipelineMessage<EnvelopeState> Message(int count, string owner) =>
        new(PipelineRuntime.Create(Guid.CreateVersion7()), new EnvelopeState(count, owner));

    private static PipelineRuntime UpdatedRuntime(PipelineRuntime runtime, string id) =>
        runtime
            .WithSession(id, JsonSerializer.SerializeToElement(new { id }))
            .WithUsage(id, new AgentUsage(1, 2, 3, 100, 80, TimeSpan.FromMilliseconds(4)))
            .WithProfile(id, new AgentProfileDecision("profile-" + id, "test"))
            .IncrementInvocations(id);

    private static BlockOutcome Outcome(string id) =>
        new("test.updated", id, "Updated envelope", JsonSerializer.SerializeToElement(new { id }));

    private static async Task AssertOperationOutsideExecutionFailsAsync()
    {
        var act = async () =>
            await PipelineOperation.RunAsync(
                () => ValueTask.FromResult(Message(0, "misuse")),
                result => result.State
            );

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*active generated pipeline step*");
    }

    private static async Task<PipelineMessage<EnvelopeState>> RunAsync(
        Pipeline pipeline,
        PipelineMessage<EnvelopeState> input
    )
    {
        var (output, failure) = await ExecuteAsync(pipeline, input, CancellationToken.None);
        failure.Should().BeNull();
        output.Should().NotBeNull();
        return output!;
    }

    private static async Task RunForFailureAsync(
        Pipeline pipeline,
        PipelineMessage<EnvelopeState> input,
        CancellationToken cancellationToken
    )
    {
        var (_, failure) = await ExecuteAsync(pipeline, input, cancellationToken);
        failure.Should().NotBeNull();
    }

    private static async Task<(
        PipelineMessage<EnvelopeState>? Output,
        Exception? Failure
    )> ExecuteAsync(
        Pipeline pipeline,
        PipelineMessage<EnvelopeState> input,
        CancellationToken cancellationToken
    )
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            pipeline.Workflow,
            input,
            "envelope-" + Guid.NewGuid().ToString("N"),
            cancellationToken
        );
        PipelineMessage<EnvelopeState>? output = null;
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
                && workflowOutput.Is<PipelineMessage<EnvelopeState>>()
            )
            {
                output = workflowOutput.As<PipelineMessage<EnvelopeState>>();
            }
        }

        return (output, failure);
    }
}

public sealed record EnvelopeState(int Count, string Owner);

[PipelineStage("envelope-state")]
public sealed partial class EnvelopeStateStage(PipelineMessage<EnvelopeState> operationMessage)
{
    public async ValueTask<EnvelopeState> ExecuteAsync(EnvelopeState state, CancellationToken _)
    {
        await PipelineOperation.RunAsync(
            () => ValueTask.FromResult(operationMessage),
            result => result.State
        );
        return state with { Count = state.Count + 1 };
    }
}

[PipelineStage("envelope-custom")]
public sealed partial class EnvelopeCustomStage(PipelineMessage<EnvelopeState> operationMessage)
{
    public ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken _
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            pipeline => ValueTask.FromResult(operationMessage),
            result => new Outcome<EnvelopeState>.Success(result.State)
        );
}

[PipelineStage("nested-envelope")]
public sealed partial class NestedEnvelopeStage(PipelineMessage<EnvelopeState> outerResult)
{
    public async ValueTask<EnvelopeState> ExecuteAsync(EnvelopeState state, CancellationToken _)
    {
        var innerInput = new PipelineMessage<EnvelopeState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            state with
            {
                Owner = "inner",
            }
        );
        var inner = new EnvelopeStateStage(
            innerInput with
            {
                Runtime = UpdatedInnerRuntime(innerInput.Runtime),
                LatestOutcome = new BlockOutcome(
                    "test.updated",
                    "inner",
                    "Updated envelope",
                    JsonSerializer.SerializeToElement(new { id = "inner" })
                ),
            }
        );
        await RunInnerAsync(TandemWorkflow.Start(inner, "inner-envelope").Build(inner), innerInput);
        return await PipelineOperation.RunAsync(
            () => ValueTask.FromResult(outerResult),
            result => result.State
        );
    }

    private static PipelineRuntime UpdatedInnerRuntime(PipelineRuntime runtime) =>
        runtime.WithSession("inner", JsonSerializer.SerializeToElement(new { id = "inner" }));

    private static async Task RunInnerAsync(Pipeline pipeline, PipelineMessage<EnvelopeState> input)
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            pipeline.Workflow,
            input,
            "inner-envelope-" + Guid.NewGuid().ToString("N"),
            CancellationToken.None
        );
        await foreach (var _ in run.WatchStreamAsync(CancellationToken.None)) { }
    }
}

[PipelineStage("parallel-envelope")]
public sealed partial class ParallelEnvelopeStage(
    PipelineMessage<EnvelopeState> operationMessage,
    EnvelopeGate gate
)
{
    public async ValueTask<EnvelopeState> ExecuteAsync(
        EnvelopeState _,
        CancellationToken cancellationToken
    )
    {
        await gate.SignalAndWaitAsync();
        return await PipelineOperation.RunAsync(
            () => ValueTask.FromResult(operationMessage),
            result => result.State
        );
    }
}

public sealed class EnvelopeGate(int participants)
{
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _arrived;

    public Task SignalAndWaitAsync()
    {
        if (Interlocked.Increment(ref _arrived) == participants)
        {
            _ready.SetResult();
        }
        return _ready.Task;
    }
}

[PipelineStage("concurrent-operation-envelope")]
public sealed partial class ConcurrentOperationEnvelopeStage(
    PipelineMessage<EnvelopeState> operationMessage
)
{
    public async ValueTask<EnvelopeState> ExecuteAsync(EnvelopeState state, CancellationToken _)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = PipelineOperation
            .RunAsync(
                async () =>
                {
                    entered.SetResult();
                    await release.Task;
                    return operationMessage;
                },
                result => result.State
            )
            .AsTask();

        await entered.Task;
        string rejection;
        try
        {
            await PipelineOperation.RunAsync(
                () => ValueTask.FromResult(operationMessage),
                result => result.State
            );
            throw new InvalidOperationException(
                "Expected the concurrent operation to be rejected."
            );
        }
        catch (InvalidOperationException exception)
        {
            rejection = exception.Message;
        }
        finally
        {
            release.SetResult();
        }

        await first;
        var final = await PipelineOperation.RunAsync(
            () => ValueTask.FromResult(operationMessage with { State = state with { Count = 3 } }),
            result => result.State
        );
        return final with { Owner = rejection };
    }
}

[PipelineStage("releasing-operation-envelope")]
public sealed partial class ReleasingOperationEnvelopeStage(
    PipelineMessage<EnvelopeState> operationMessage
)
{
    public async ValueTask<EnvelopeState> ExecuteAsync(EnvelopeState _, CancellationToken __)
    {
        try
        {
            await PipelineOperation.RunAsync<EnvelopeState, EnvelopeState>(
                () =>
                    ValueTask.FromException<PipelineMessage<EnvelopeState>>(
                        new InvalidOperationException("Expected operation failure.")
                    ),
                result => result.State
            );
        }
        catch (InvalidOperationException) { }

        try
        {
            await PipelineOperation.RunAsync<EnvelopeState, EnvelopeState>(
                () =>
                    ValueTask.FromCanceled<PipelineMessage<EnvelopeState>>(
                        new CancellationToken(canceled: true)
                    ),
                result => result.State
            );
        }
        catch (OperationCanceledException) { }

        return await PipelineOperation.RunAsync(
            () => ValueTask.FromResult(operationMessage),
            result => result.State
        );
    }
}

[PipelineStage("cancelled-envelope")]
public sealed partial class CancelledEnvelopeStage
{
    public ValueTask ExecuteAsync(EnvelopeState _, CancellationToken cancellationToken)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        return ValueTask.FromCanceled(cancellation.Token);
    }
}

[PipelineStage("faulted-envelope")]
public sealed partial class FaultedEnvelopeStage
{
    public ValueTask ExecuteAsync(EnvelopeState _, CancellationToken cancellationToken) =>
        ValueTask.FromException(new InvalidOperationException("Expected envelope test fault."));
}
