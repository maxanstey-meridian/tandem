using FluentAssertions;

namespace Tandem.Tests.Composition;

public sealed class ExecutionEnvelopeInvariantTests
{
    [Fact]
    public async Task SupportedOperationPath_PropagatesStateAndOutcomeEnvelope()
    {
        var runId = Guid.CreateVersion7();
        var step = new EnvelopeOperationStage();

        var result = await RunAsync(step, new EnvelopeState(2, "initial"), runId);

        result.State.Should().Be(new EnvelopeState(4, "first-operation", runId));
        result.Outcome!.StepId.Should().Be("envelope-operation");
        result.Outcome.Kind.Should().Be(StandardOutcomeKinds.Success);
    }

    [Fact]
    public async Task NestedGeneratedExecution_RestoresOuterScope()
    {
        var outerRunId = Guid.CreateVersion7();
        var step = new NestedEnvelopeStage();

        var result = await RunAsync(step, new EnvelopeState(1, "outer"), outerRunId);

        result.State.Should().Be(new EnvelopeState(3, "outer", outerRunId));
    }

    [Fact]
    public async Task ParallelPipelineExecutions_DoNotCrossContaminateScopes()
    {
        var gate = new EnvelopeGate(2);
        var firstRunId = Guid.CreateVersion7();
        var secondRunId = Guid.CreateVersion7();
        var first = new ParallelEnvelopeStage(gate);
        var second = new ParallelEnvelopeStage(gate);

        var results = await Task.WhenAll(
            RunAsync(first, new EnvelopeState(1, "first"), firstRunId),
            RunAsync(second, new EnvelopeState(10, "second"), secondRunId)
        );

        results[0].State.Should().Be(new EnvelopeState(2, "first", firstRunId));
        results[1].State.Should().Be(new EnvelopeState(11, "second", secondRunId));
    }

    [Fact]
    public async Task ConcurrentOperationsWithinGeneratedExecution_AreRejectedDeterministically()
    {
        var step = new ConcurrentOperationEnvelopeStage();

        var result = await RunAsync(step, new EnvelopeState(1, "siblings"));

        result.State.Count.Should().Be(3);
        result
            .State.Owner.Should()
            .Be(
                "Concurrent sibling operations cannot run within the same generated pipeline step. Await the active operation before starting another."
            );
    }

    [Fact]
    public async Task FailedAndCancelledOperations_ReleaseOperationGuard()
    {
        var step = new ReleasingOperationEnvelopeStage();

        var result = await RunAsync(step, new EnvelopeState(1, "release"));

        result.State.Should().Be(new EnvelopeState(2, "release"));
    }

    [Fact]
    public async Task Cancellation_ClearsExecutionScope()
    {
        var step = new CancelledEnvelopeStage();
        var run = async () => await RunAsync(step, new EnvelopeState(0, "cancelled"));

        var exception = await run.Should().ThrowAsync<PipelineRunException>();
        exception.Which.InnerException.Should().BeAssignableTo<OperationCanceledException>();
        await AssertOperationOutsideExecutionFailsAsync();
    }

    [Fact]
    public async Task Exception_ClearsExecutionScope()
    {
        var step = new FaultedEnvelopeStage();
        var run = async () => await RunAsync(step, new EnvelopeState(0, "faulted"));

        var exception = await run.Should().ThrowAsync<PipelineRunException>();
        exception
            .Which.InnerException.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Be("Expected fault.");
        await AssertOperationOutsideExecutionFailsAsync();
    }

    [Fact]
    public async Task OperationOutsideGeneratedExecution_FailsClearly()
    {
        await AssertOperationOutsideExecutionFailsAsync();
    }

    private static Task<PipelineRunResult<EnvelopeState>> RunAsync<TStep>(
        TStep step,
        EnvelopeState state,
        Guid? runId = null
    )
        where TStep : IGeneratedPipelineStep<EnvelopeState, Outcome<EnvelopeState>> =>
        new PipelineRunner().RunAsync(
            Pipeline.Start(step, "execution-envelope").Build(step),
            state,
            new PipelineRunOptions(RunId: runId)
        );

    private static async Task AssertOperationOutsideExecutionFailsAsync()
    {
        var act = async () =>
            await PipelineOperation.RunOutcomeAsync(
                new EnvelopeState(0, "outside"),
                context => ValueTask.FromResult(Result(context.State, "outside")),
                Success
            );

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*active generated pipeline step*");
    }

    internal static OperationResult<EnvelopeState> Result(EnvelopeState state, string stepId) =>
        new(state, new OperationOutcome("test.operation", stepId, stepId));

    internal static Outcome<EnvelopeState> Success(OperationResult<EnvelopeState> result) =>
        new Outcome<EnvelopeState>.Success(result.State);
}

public sealed record EnvelopeState(int Count, string Owner, Guid? ObservedRunId = null);

[PipelineStage("envelope-operation")]
public sealed partial class EnvelopeOperationStage
{
    public async ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken _
    )
    {
        var first = await PipelineOperation.RunOutcomeAsync(
            state,
            context =>
                ValueTask.FromResult(
                    ExecutionEnvelopeInvariantTests.Result(
                        context.State with
                        {
                            Count = context.State.Count + 1,
                            ObservedRunId = context.RunId,
                        },
                        "first-operation"
                    )
                ),
            ExecutionEnvelopeInvariantTests.Success
        );

        var firstState = first is Outcome<EnvelopeState>.Success success
            ? success.State
            : throw new InvalidOperationException("Expected the first operation to succeed.");
        return await PipelineOperation.RunOutcomeAsync(
            firstState,
            context =>
            {
                if (context.LatestOutcome?.StepId != "first-operation")
                {
                    throw new InvalidOperationException(
                        "The prior operation outcome was not propagated."
                    );
                }
                return ValueTask.FromResult(
                    ExecutionEnvelopeInvariantTests.Result(
                        context.State with
                        {
                            Count = context.State.Count + 1,
                            Owner = context.LatestOutcome.StepId,
                        },
                        "second-operation"
                    )
                );
            },
            ExecutionEnvelopeInvariantTests.Success
        );
    }
}

[PipelineStage("nested-envelope")]
public sealed partial class NestedEnvelopeStage
{
    public async ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken cancellationToken
    )
    {
        var inner = new InnerEnvelopeStage();
        await new PipelineRunner().RunAsync(
            Pipeline.Start(inner, "inner-envelope").Build(inner),
            new EnvelopeState(20, "inner"),
            cancellationToken: cancellationToken
        );

        return await PipelineOperation.RunOutcomeAsync(
            state,
            context =>
                ValueTask.FromResult(
                    ExecutionEnvelopeInvariantTests.Result(
                        context.State with
                        {
                            Count = context.State.Count + 2,
                            ObservedRunId = context.RunId,
                        },
                        "outer-after-inner"
                    )
                ),
            ExecutionEnvelopeInvariantTests.Success
        );
    }
}

[PipelineStage("inner-envelope")]
public sealed partial class InnerEnvelopeStage
{
    public ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken _
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            context =>
                ValueTask.FromResult(
                    ExecutionEnvelopeInvariantTests.Result(
                        context.State with
                        {
                            Count = context.State.Count + 1,
                        },
                        "inner"
                    )
                ),
            ExecutionEnvelopeInvariantTests.Success
        );
}

[PipelineStage("parallel-envelope")]
public sealed partial class ParallelEnvelopeStage(EnvelopeGate gate)
{
    public async ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken _
    )
    {
        await gate.SignalAndWaitAsync();
        return await PipelineOperation.RunOutcomeAsync(
            state,
            context =>
                ValueTask.FromResult(
                    ExecutionEnvelopeInvariantTests.Result(
                        context.State with
                        {
                            Count = context.State.Count + 1,
                            ObservedRunId = context.RunId,
                        },
                        context.State.Owner
                    )
                ),
            ExecutionEnvelopeInvariantTests.Success
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
public sealed partial class ConcurrentOperationEnvelopeStage
{
    public async ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken _
    )
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = PipelineOperation
            .RunOutcomeAsync(
                state,
                async context =>
                {
                    entered.SetResult();
                    await release.Task;
                    return ExecutionEnvelopeInvariantTests.Result(
                        context.State with
                        {
                            Count = 2,
                        },
                        "first-sibling"
                    );
                },
                ExecutionEnvelopeInvariantTests.Success
            )
            .AsTask();

        await entered.Task;
        string rejection;
        try
        {
            await PipelineOperation.RunOutcomeAsync(
                state,
                context =>
                    ValueTask.FromResult(
                        ExecutionEnvelopeInvariantTests.Result(context.State, "second-sibling")
                    ),
                ExecutionEnvelopeInvariantTests.Success
            );
            throw new InvalidOperationException("Expected the sibling operation to be rejected.");
        }
        catch (InvalidOperationException exception)
        {
            rejection = exception.Message;
        }
        finally
        {
            release.SetResult();
        }

        var firstResult = await first;
        var firstState = firstResult is Outcome<EnvelopeState>.Success success
            ? success.State
            : throw new InvalidOperationException("Expected the first sibling to succeed.");
        return await PipelineOperation.RunOutcomeAsync(
            firstState,
            context =>
                ValueTask.FromResult(
                    ExecutionEnvelopeInvariantTests.Result(
                        context.State with
                        {
                            Count = 3,
                            Owner = rejection,
                        },
                        "after-siblings"
                    )
                ),
            ExecutionEnvelopeInvariantTests.Success
        );
    }
}

[PipelineStage("releasing-operation-envelope")]
public sealed partial class ReleasingOperationEnvelopeStage
{
    public async ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken _
    )
    {
        try
        {
            await PipelineOperation.RunOutcomeAsync(
                state,
                _ =>
                    ValueTask.FromException<OperationResult<EnvelopeState>>(
                        new InvalidOperationException("Expected operation failure.")
                    ),
                ExecutionEnvelopeInvariantTests.Success
            );
        }
        catch (InvalidOperationException) { }

        try
        {
            await PipelineOperation.RunOutcomeAsync(
                state,
                _ =>
                    ValueTask.FromCanceled<OperationResult<EnvelopeState>>(
                        new CancellationToken(canceled: true)
                    ),
                ExecutionEnvelopeInvariantTests.Success
            );
        }
        catch (OperationCanceledException) { }

        return await PipelineOperation.RunOutcomeAsync(
            state,
            context =>
                ValueTask.FromResult(
                    ExecutionEnvelopeInvariantTests.Result(
                        context.State with
                        {
                            Count = context.State.Count + 1,
                        },
                        "after-failure-and-cancellation"
                    )
                ),
            ExecutionEnvelopeInvariantTests.Success
        );
    }
}

[PipelineStage("cancelled-envelope")]
public sealed partial class CancelledEnvelopeStage
{
    public ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken _
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            _ =>
                ValueTask.FromCanceled<OperationResult<EnvelopeState>>(
                    new CancellationToken(canceled: true)
                ),
            ExecutionEnvelopeInvariantTests.Success
        );
}

[PipelineStage("faulted-envelope")]
public sealed partial class FaultedEnvelopeStage
{
    public ValueTask<Outcome<EnvelopeState>> ExecuteAsync(
        EnvelopeState state,
        CancellationToken _
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            _ =>
                ValueTask.FromException<OperationResult<EnvelopeState>>(
                    new InvalidOperationException("Expected fault.")
                ),
            ExecutionEnvelopeInvariantTests.Success
        );
}
