using System.Text.Json;
using FluentAssertions;
using Tandem.Domain;

namespace Tandem.Tests.Composition;

public sealed class ParallelPipelineRunnerTests
{
    [Fact]
    public async Task BranchesOverlapAndMergeInDeclarationOrder()
    {
        var entered = 0;
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var first = PipelineNodes.Stage<ParallelState>(
            "first",
            async (state, cancellationToken) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.SetResult();
                }
                await bothEntered.Task.WaitAsync(cancellationToken);
                state.Values.Add("first");
                return state;
            }
        );
        var second = PipelineNodes.Stage<ParallelState>(
            "second",
            async (state, cancellationToken) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.SetResult();
                }
                await bothEntered.Task.WaitAsync(cancellationToken);
                state.Values.Add("second");
                return state;
            }
        );
        var parallel = PipelineNodes.Parallel(
            "parallel",
            state => state with { Values = [.. state.Values] },
            [PipelineBranch.Create("one", first), PipelineBranch.Create("two", second)],
            results =>
                results.Baseline with
                {
                    Values = [.. results.State("one").Values, .. results.State("two").Values],
                }
        );
        var complete = PipelineNodes.Stage<ParallelState>(
            "complete",
            (state, _) => ValueTask.FromResult(state)
        );
        var pipeline = Pipeline
            .Start(parallel, "parallel-overlap")
            .Route(parallel.Success, complete, "merged")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new ParallelState([]),
            cancellationToken: CancellationToken.None
        );

        result.Succeeded.Should().BeTrue();
        result.State.Values.Should().Equal("first", "second");
    }

    [Fact]
    public async Task DeclaredFailureSkipsMergeAndUsesDeclarationOrder()
    {
        var mergeCalled = false;
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var first = new InlineOutcomeStep(
            "first-failure",
            new FailureEvidence("first", "First failure"),
            async cancellationToken => await releaseFirst.Task.WaitAsync(cancellationToken)
        );
        var second = new InlineOutcomeStep(
            "second-failure",
            new FailureEvidence("second", "Second failure"),
            _ =>
            {
                secondCompleted.SetResult();
                releaseFirst.SetResult();
                return ValueTask.CompletedTask;
            }
        );
        var parallel = PipelineNodes.Parallel(
            "parallel",
            state => state with { Values = [.. state.Values] },
            [PipelineBranch.Create("one", first), PipelineBranch.Create("two", second)],
            results =>
            {
                mergeCalled = true;
                return results.Baseline;
            }
        );
        var failed = PipelineNodes.Stage<ParallelState>(
            "failed",
            (state, _) => ValueTask.FromResult(state)
        );
        var pipeline = Pipeline
            .Start(parallel, "parallel-failure")
            .Route(parallel.Failed, failed, "failed")
            .Build(failed);
        var observer = new RecordingObserver();

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new ParallelState([]),
            new PipelineRunOptions(Observer: observer),
            CancellationToken.None
        );

        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        mergeCalled.Should().BeFalse();
        result.Succeeded.Should().BeTrue();
        result.Outcome!.StepId.Should().Be("failed");
        var groupOutcome = observer
            .Observations.OfType<PipelineStepCompleted>()
            .Single(value => value.StepId == "parallel")
            .Outcome;
        groupOutcome.Summary.Should().Be("First failure");
        groupOutcome
            .Payload.Deserialize<FailureEvidence>()
            .Should()
            .Be(new FailureEvidence("first", "First failure"));
    }

    [Fact]
    public void ParallelRequiresTwoUniqueOwnedBranches()
    {
        var stage = PipelineNodes.Stage<ParallelState>(
            "stage",
            (state, _) => ValueTask.FromResult(state)
        );

        var oneBranch = () =>
            PipelineNodes.Parallel(
                "parallel",
                state => state,
                [PipelineBranch.Create("one", stage)],
                results => results.Baseline
            );
        var duplicateParticipant = () =>
            PipelineNodes.Parallel(
                "parallel",
                state => state,
                [PipelineBranch.Create("one", stage), PipelineBranch.Create("two", stage)],
                results => results.Baseline
            );

        oneBranch.Should().Throw<ArgumentException>().WithMessage("*at least two*");
        duplicateParticipant
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*only one parallel branch*");
    }

    [Fact]
    public void InspectionShowsSemanticGroupAndBranchesWithoutPhysicalHelpers()
    {
        var first = PipelineNodes.Stage<ParallelState>(
            "first",
            (state, _) => ValueTask.FromResult(state)
        );
        var second = PipelineNodes.Stage<ParallelState>(
            "second",
            (state, _) => ValueTask.FromResult(state)
        );
        var parallel = PipelineNodes.Parallel(
            "parallel",
            state => state with { Values = [.. state.Values] },
            [PipelineBranch.Create("one", first), PipelineBranch.Create("two", second)],
            results => results.Baseline
        );
        var complete = PipelineNodes.Stage<ParallelState>(
            "parallel--cleanup",
            (state, _) => ValueTask.FromResult(state)
        );

        var inspection = Pipeline
            .Start(parallel, "parallel-inspection")
            .Route(parallel.Success, complete, "complete")
            .Build(complete)
            .Inspect();

        inspection
            .StepIds.Should()
            .BeEquivalentTo("parallel", "first", "second", "parallel--cleanup");
        inspection
            .ParallelGroups.Should()
            .ContainSingle()
            .Which.Branches.Should()
            .Equal(
                new PipelineParallelBranchInspection("one", 0, "first"),
                new PipelineParallelBranchInspection("two", 1, "second")
            );
        inspection.Mermaid.Should().Contain("|\"one\"|").And.Contain("|\"two\"|");
        inspection.Dot.Should().Contain("label=\"one\"").And.Contain("label=\"two\"");
    }

    [Fact]
    public void AuthoredParticipantIdsCannotCollideWithPhysicalParallelIds()
    {
        var first = Stage("first");
        var second = Stage("second");
        var parallel = Parallel("parallel", first, second);
        var colliding = Stage("parallel--fork");

        var physicalFirst = () =>
            Pipeline
                .Start(parallel, "physical-first")
                .Route(parallel.Success, colliding, "collision")
                .Build(colliding);
        var authoredFirst = () =>
            Pipeline
                .Start(colliding, "authored-first")
                .Route(colliding, parallel, "collision")
                .Build(parallel);

        physicalFirst.Should().Throw<InvalidOperationException>().WithMessage("*globally unique*");
        authoredFirst.Should().Throw<InvalidOperationException>().WithMessage("*conflicts*");
    }

    [Fact]
    public async Task BranchFaultTerminalizesTheParallelGroupObservation()
    {
        var fault = PipelineNodes.Stage<ParallelState>(
            "fault",
            (_, _) =>
                ValueTask.FromException<ParallelState>(
                    new InvalidOperationException("branch failed")
                )
        );
        var sibling = PipelineNodes.Stage<ParallelState>(
            "sibling",
            (state, _) => ValueTask.FromResult(state)
        );
        var parallel = PipelineNodes.Parallel(
            "parallel",
            state => state with { Values = [.. state.Values] },
            [PipelineBranch.Create("fault", fault), PipelineBranch.Create("sibling", sibling)],
            results => results.Baseline
        );
        var complete = PipelineNodes.Stage<ParallelState>(
            "complete",
            (state, _) => ValueTask.FromResult(state)
        );
        var observer = new RecordingObserver();
        var pipeline = Pipeline
            .Start(parallel, "parallel-fault")
            .Route(parallel.Success, complete, "complete")
            .Build(complete);

        var run = () =>
            new PipelineRunner().RunAsync(
                pipeline,
                new ParallelState([]),
                new PipelineRunOptions(Observer: observer)
            );

        await run.Should().ThrowAsync<PipelineRunException>();
        observer
            .Observations.Count(value => value is PipelineStepStarted { StepId: "parallel" })
            .Should()
            .Be(1);
        observer
            .Observations.Count(value => value is PipelineStepFaulted { StepId: "parallel" })
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task WrappedBranchCancellationCancelsTheParallelGroupObservation()
    {
        var cancelled = PipelineNodes.Stage<ParallelState>(
            "cancelled",
            (_, _) => ValueTask.FromException<ParallelState>(new OperationCanceledException("stop"))
        );
        var sibling = Stage("sibling");
        var parallel = Parallel("parallel", cancelled, sibling);
        var complete = Stage("complete");
        var observer = new RecordingObserver();
        var pipeline = Pipeline
            .Start(parallel, "parallel-cancelled")
            .Route(parallel.Success, complete, "complete")
            .Build(complete);

        var run = () =>
            new PipelineRunner().RunAsync(
                pipeline,
                new ParallelState([]),
                new PipelineRunOptions(Observer: observer)
            );

        await run.Should().ThrowAsync<PipelineRunException>();
        observer
            .Observations.OfType<PipelineStepCancelled>()
            .Select(value => value.StepId)
            .Should()
            .BeEquivalentTo("parallel", "cancelled");
        observer
            .Observations.OfType<PipelineStepFaulted>()
            .Should()
            .NotContain(value => value.StepId == "parallel");
    }

    [Fact]
    public async Task CloneFailureFaultsBeforeAnyBranchRuns()
    {
        var cloneCalls = 0;
        var branchCalls = 0;
        var mergeCalls = 0;
        var first = PipelineNodes.Stage<ParallelState>(
            "first",
            (state, _) =>
            {
                branchCalls++;
                return ValueTask.FromResult(state);
            }
        );
        var second = PipelineNodes.Stage<ParallelState>(
            "second",
            (state, _) =>
            {
                branchCalls++;
                return ValueTask.FromResult(state);
            }
        );
        var parallel = PipelineNodes.Parallel(
            "parallel",
            state =>
            {
                cloneCalls++;
                return cloneCalls == 2
                    ? throw new InvalidOperationException("clone failed")
                    : state with
                    {
                        Values = [.. state.Values],
                    };
            },
            [PipelineBranch.Create("one", first), PipelineBranch.Create("two", second)],
            results =>
            {
                mergeCalls++;
                return results.Baseline;
            }
        );
        var complete = Stage("complete");
        var observer = new RecordingObserver();
        var pipeline = Pipeline
            .Start(parallel, "clone-failure")
            .Route(parallel.Success, complete, "complete")
            .Build(complete);

        var run = () =>
            new PipelineRunner().RunAsync(
                pipeline,
                new ParallelState([]),
                new PipelineRunOptions(Observer: observer)
            );

        await run.Should().ThrowAsync<PipelineRunException>();
        cloneCalls.Should().Be(2);
        branchCalls.Should().Be(0);
        mergeCalls.Should().Be(0);
        observer
            .Observations.OfType<PipelineStepFaulted>()
            .Should()
            .ContainSingle(value => value.StepId == "parallel");
    }

    [Fact]
    public async Task ConcurrentRunsOfOnePipelineKeepBranchStateAndOccurrencesIsolated()
    {
        var entered = 0;
        var allEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        IGeneratedPipelineStep<ParallelState, GeneratedStepCompletion> Branch(string id) =>
            PipelineNodes.Stage<ParallelState>(
                id,
                async (state, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref entered) == 4)
                    {
                        allEntered.SetResult();
                    }
                    await allEntered.Task.WaitAsync(cancellationToken);
                    state.Values.Add($"{state.Values[0]}-{id}");
                    return state;
                }
            );
        var parallel = PipelineNodes.Parallel(
            "parallel",
            state => state with { Values = [.. state.Values] },
            [
                PipelineBranch.Create("one", Branch("first")),
                PipelineBranch.Create("two", Branch("second")),
            ],
            results =>
                results.Baseline with
                {
                    Values = [.. results.State("one").Values, .. results.State("two").Values],
                }
        );
        var complete = Stage("complete");
        var pipeline = Pipeline
            .Start(parallel, "concurrent-runs")
            .Route(parallel.Success, complete, "complete")
            .Build(complete);

        var runs = await Task.WhenAll(
            new PipelineRunner().RunAsync(pipeline, new ParallelState(["alpha"])),
            new PipelineRunner().RunAsync(pipeline, new ParallelState(["beta"]))
        );

        runs[0].State.Values.Should().OnlyContain(value => value.StartsWith("alpha"));
        runs[1].State.Values.Should().OnlyContain(value => value.StartsWith("beta"));
    }

    [Fact]
    public async Task ParallelGroupCanBeVisitedRepeatedlyWithoutStaleBranchResults()
    {
        var visits = 0;
        var first = Stage("first");
        var second = Stage("second");
        var parallel = PipelineNodes.Parallel(
            "parallel",
            state => state with { Values = [.. state.Values] },
            [PipelineBranch.Create("one", first), PipelineBranch.Create("two", second)],
            results =>
            {
                visits++;
                return results.Baseline with
                {
                    Values = [.. results.Baseline.Values, $"first-{visits}", $"second-{visits}"],
                };
            }
        );
        var complete = Stage("complete");
        var observer = new RecordingObserver();
        var pipeline = Pipeline
            .Start(parallel, "repeated-parallel")
            .Route(parallel.Success, state => state.Values.Count < 6, parallel, "repeat")
            .Route(parallel.Success, state => state.Values.Count == 6, complete, "complete")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new ParallelState([]),
            new PipelineRunOptions(Observer: observer)
        );

        result
            .State.Values.Should()
            .Equal("first-1", "second-1", "first-2", "second-2", "first-3", "second-3");
        observer
            .Observations.Count(value => value is PipelineStepCompleted { StepId: "parallel" })
            .Should()
            .Be(3);
    }

    [Fact]
    public async Task ObserverAndAcceptanceUnitOfWorkCallbacksAreSerialized()
    {
        var observer = new ConcurrencyObserver();
        var unitOfWork = new ConcurrencyUnitOfWork();
        var context = new PipelineRunContext(Guid.CreateVersion7(), observer, unitOfWork);

        await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(index =>
                    context
                        .ObserveAsync(
                            new PipelineStepStarted(context.RunId, $"step-{index}"),
                            CancellationToken.None
                        )
                        .AsTask()
                )
        );
        await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(index =>
                    context
                        .ExecuteAsync(
                            async cancellationToken =>
                            {
                                await Task.Delay(10, cancellationToken);
                                return index;
                            },
                            CancellationToken.None
                        )
                        .AsTask()
                )
        );

        observer.MaximumConcurrency.Should().Be(1);
        unitOfWork.MaximumConcurrency.Should().Be(1);
        unitOfWork.ExecutionCount.Should().Be(4);
    }

    [Fact]
    public async Task PersistentParallelRecordsBranchAndMergedAcceptedValues()
    {
        var first = PipelineNodes.Stage<ParallelState>(
            "first",
            (state, _) => ValueTask.FromResult(state with { Values = [.. state.Values, "first"] })
        );
        var second = PipelineNodes.Stage<ParallelState>(
            "second",
            (state, _) => ValueTask.FromResult(state with { Values = [.. state.Values, "second"] })
        );
        var parallel = PipelineNodes.Parallel(
            "parallel",
            state => state with { Values = [.. state.Values] },
            [PipelineBranch.Create("one", first), PipelineBranch.Create("two", second)],
            results =>
                results.Baseline with
                {
                    Values = [.. results.State("one").Values, .. results.State("two").Values],
                }
        );
        var observer = new RecordingObserver();
        var complete = Stage("complete");
        var pipeline = Pipeline
            .Start(parallel, "persistent-parallel")
            .Persist()
            .DoNotPersist(complete)
            .Route(parallel.Success, complete, "complete")
            .Build(complete);

        await new PipelineRunner().RunAsync(
            pipeline,
            new ParallelState([]),
            new PipelineRunOptions(Observer: observer)
        );

        var accepted = observer
            .Observations.OfType<PipelineStepCompleted>()
            .Where(value => value.AcceptedValue is not null)
            .ToDictionary(value => value.StepId, value => value.AcceptedValue!);
        accepted.Keys.Should().BeEquivalentTo("first", "second", "parallel");
        accepted["first"]
            .Payload.Deserialize<ParallelState>(JsonSerializerOptions.Web)!
            .Values.Should()
            .Equal("first");
        accepted["second"]
            .Payload.Deserialize<ParallelState>(JsonSerializerOptions.Web)!
            .Values.Should()
            .Equal("second");
        accepted["parallel"]
            .Payload.Deserialize<ParallelState>(JsonSerializerOptions.Web)!
            .Values.Should()
            .Equal("first", "second");
    }

    [Fact]
    public void RuntimeMergeRejectsConflictingBranchDeltas()
    {
        var baseline = PipelineRuntime.Create(Guid.CreateVersion7());
        var first = baseline.IncrementInvocations("shared");
        var second = first.IncrementInvocations("shared");

        var merge = () => PipelineRuntime.Merge(baseline, [first, second]);

        merge
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*conflicting changes*shared*");
    }

    public sealed record ParallelState(List<string> Values);

    private static IGeneratedPipelineStep<ParallelState, GeneratedStepCompletion> Stage(
        string id
    ) => PipelineNodes.Stage<ParallelState>(id, (state, _) => ValueTask.FromResult(state));

    private static PipelineParallel<ParallelState> Parallel(
        string id,
        IGeneratedPipelineStep<ParallelState, GeneratedStepCompletion> first,
        IGeneratedPipelineStep<ParallelState, GeneratedStepCompletion> second
    ) =>
        PipelineNodes.Parallel(
            id,
            state => state with { Values = [.. state.Values] },
            [PipelineBranch.Create("one", first), PipelineBranch.Create("two", second)],
            results => results.Baseline
        );

    private sealed class InlineOutcomeStep : IStandardOutcomePipelineStep<ParallelState>
    {
        public InlineOutcomeStep(
            string id,
            FailureEvidence failure,
            Func<CancellationToken, ValueTask>? before = null
        )
        {
            Id = id;
            Descriptor = new GeneratedOutcomeStepDescriptor<ParallelState>(
                id,
                async (state, cancellationToken) =>
                {
                    if (before is not null)
                    {
                        await before(cancellationToken);
                    }
                    return new Outcome<ParallelState>.Failed(state, failure);
                }
            );
        }

        public string Id { get; }
        public PipelineNodeDescriptor Descriptor { get; }
        public PipelineOutcomeSelector<ParallelState> Success => new(this, failed: false);
        public PipelineOutcomeSelector<ParallelState> Failed => new(this, failed: true);
    }

    private sealed class RecordingObserver : IPipelinePersistenceObserver
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<PipelineObservation> _values =
            new();

        public IReadOnlyCollection<PipelineObservation> Observations => _values.ToArray();

        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            _values.Enqueue(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConcurrencyObserver : IPipelineObserver
    {
        private int _active;
        private int _maximumConcurrency;
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            var active = Interlocked.Increment(ref _active);
            RecordMaximum(ref _maximumConcurrency, active);
            try
            {
                await Task.Delay(10, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class ConcurrencyUnitOfWork : IPipelineAcceptanceUnitOfWork
    {
        private int _active;
        private int _executionCount;
        private int _maximumConcurrency;
        public int ExecutionCount => Volatile.Read(ref _executionCount);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async ValueTask<T> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _executionCount);
            var active = Interlocked.Increment(ref _active);
            RecordMaximum(ref _maximumConcurrency, active);
            try
            {
                await Task.Delay(10, cancellationToken);
                return await operation(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static void RecordMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed)
            {
                return;
            }
            observed = previous;
        }
    }
}
