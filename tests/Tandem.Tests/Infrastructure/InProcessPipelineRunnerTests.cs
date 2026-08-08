using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class InProcessPipelineRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsTypedPipelineOutput()
    {
        var increment = new IncrementStage();
        var pipeline = Pipeline.Start(increment, "in-process-completion").Build(increment);
        var runId = Guid.CreateVersion7();

        var output = await new InProcessPipelineRunner().RunAsync(
            pipeline,
            runId,
            new RunnerState(1),
            CancellationToken.None
        );

        output.State.Count.Should().Be(2);
        output.Runtime.RunId.Should().Be(runId);
    }

    [Fact]
    public async Task PersistentPipeline_RequiresPersistenceObserverBeforeExecution()
    {
        var increment = new IncrementStage();
        var pipeline = Pipeline.Start(increment, "persistent").Persist().Build(increment);

        var run = async () => await new PipelineRunner().RunAsync(pipeline, new RunnerState(1));

        await run.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires accepted-value persistence*");
    }

    [Fact]
    public async Task PersistencePolicy_IsInspectableAndSupportsStepOptOut()
    {
        var increment = new IncrementStage();
        var observations = new List<PipelineObservation>();
        var pipeline = Pipeline
            .Start(increment, "persistent-opt-out")
            .Persist()
            .DoNotPersist(increment)
            .Build(increment);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(1),
            new PipelineRunOptions(
                Observer: new InlinePersistenceObserver(observation =>
                    observations.Add(observation)
                )
            )
        );

        result.State.Count.Should().Be(2);
        pipeline.Inspect().PersistentStepIds.Should().BeEmpty();
        observations
            .OfType<PipelineStepCompleted>()
            .Should()
            .ContainSingle()
            .Which.AcceptedValue.Should()
            .BeNull();
    }

    [Fact]
    public async Task PersistenceObserver_SatisfiesPersistentPipelineRequirement()
    {
        var increment = new IncrementStage();
        var pipeline = Pipeline.Start(increment, "persistent").Persist().Build(increment);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(1),
            new PipelineRunOptions(Observer: new InlinePersistenceObserver())
        );

        result.State.Count.Should().Be(2);
        pipeline.Inspect().PersistentStepIds.Should().Equal("runner-increment");
    }

    [Fact]
    public async Task StepPersistence_EnablesOneParticipantInEphemeralPipeline()
    {
        var increment = new IncrementStage();
        var pipeline = Pipeline
            .Start(increment, "step-persistence")
            .Persist(increment)
            .Build(increment);

        await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(1),
            new PipelineRunOptions(Observer: new InlinePersistenceObserver())
        );

        pipeline.Inspect().PersistentStepIds.Should().Equal("runner-increment");
    }

    [Fact]
    public async Task PersistentStage_PersistenceFailurePreventsDownstreamRouting()
    {
        var increment = new IncrementStage();
        var completion = new ProbeCompletion();
        var complete = PipelineNodes.Complete(completion);
        var pipeline = Pipeline
            .Start(increment, "persistence-before-routing")
            .Persist(increment)
            .Route(when: _ => true, from: increment, to: complete, label: "complete")
            .Build(complete);

        var run = async () =>
            await new PipelineRunner().RunAsync(
                pipeline,
                new RunnerState(1),
                new PipelineRunOptions(
                    Observer: new FailingPersistenceObserver<PipelineStepCompleted>()
                )
            );

        await run.Should().ThrowAsync<Exception>();
        completion.ApplyCount.Should().Be(0);
    }

    [Fact]
    public void PersistenceOverride_RejectsUnregisteredParticipant()
    {
        var increment = new IncrementStage();
        var unregistered = new FaultStage();
        var builder = Pipeline.Start(increment, "invalid-persistence").Persist(unregistered);

        var build = () => builder.Build(increment);

        build
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*unregistered step 'runner-fault'*");
    }

    [Fact]
    public async Task RunAsync_ReturnsDeclaredFailureAsOutput()
    {
        var failure = new DeclaredFailureStage();
        var pipeline = Pipeline.Start(failure, "in-process-declared-failure").Build(failure);

        var output = await new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            new RunnerState(1),
            CancellationToken.None
        );

        output.Status.Should().Be(PipelineRunStatus.Failed);
        output.LatestResult!.CaseId.Should().Be(nameof(Outcome<RunnerState>.Failed));
    }

    [Fact]
    public async Task CompletionDefinition_InfersState_AppliesOnce_AndPublishesStandardCompletionOnce()
    {
        var definition = new ProbeCompletion();
        var complete = PipelineNodes.Complete(definition);
        var start = new IncrementStage();
        var pipeline = Pipeline
            .Start(start, "definition-completion")
            .Route(when: _ => true, from: start, to: complete, label: "complete")
            .Build(complete);
        var observations = new List<PipelineObservation>();

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(1),
            new PipelineRunOptions(
                Observer: new InlinePipelineObserver(observation => observations.Add(observation))
            )
        );

        definition.ApplyCount.Should().Be(1);
        result.State.Count.Should().Be(12);
        result.Status.Should().Be(PipelineRunStatus.Succeeded);
        result
            .Outcome.Should()
            .Match<PipelineRunOutcome>(outcome =>
                outcome.Kind == StandardOutcomeKinds.Success
                && outcome.StepId == definition.Id
                && outcome.Summary == "Completed at 12"
            );
        observations
            .OfType<PipelineStepCompleted>()
            .Should()
            .ContainSingle(observation =>
                observation.StepId == definition.Id
                && observation.Outcome.Kind == StandardOutcomeKinds.Success
            );
    }

    [Fact]
    public async Task FailureDefinition_InfersState_AppliesOnce_AndPublishesStandardFailureOnce()
    {
        var definition = new ProbeFailure();
        var failed = PipelineNodes.Failed(definition);
        var start = new IncrementStage();
        var pipeline = Pipeline
            .Start(start, "definition-failure")
            .Route(when: _ => true, from: start, to: failed, label: "fail")
            .Build(failed);
        var observations = new List<PipelineObservation>();

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(1),
            new PipelineRunOptions(
                Observer: new InlinePipelineObserver(observation => observations.Add(observation))
            )
        );

        definition.ApplyCount.Should().Be(1);
        result.State.Count.Should().Be(-1);
        result.Status.Should().Be(PipelineRunStatus.Failed);
        result
            .Outcome.Should()
            .Match<PipelineRunOutcome>(outcome =>
                outcome.Kind == StandardOutcomeKinds.Failed
                && outcome.StepId == definition.Id
                && outcome.Summary == "Failed at -1"
            );
        observations
            .OfType<PipelineStepCompleted>()
            .Should()
            .ContainSingle(observation =>
                observation.StepId == definition.Id
                && observation.Outcome.Kind == StandardOutcomeKinds.Failed
            );
    }

    [Fact]
    public async Task RunAsync_PropagatesExecutionFault()
    {
        var fault = new FaultStage();
        var pipeline = Pipeline.Start(fault, "in-process-fault").Build(fault);

        var act = () =>
            new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(0),
                CancellationToken.None
            );

        var exception = await act.Should().ThrowAsync<PipelineRunException>();
        exception.Which.InnerException.Should().BeOfType<ProbeException>();
    }

    [Fact]
    public async Task RunAsync_CancelsTheLiveWorkflow()
    {
        var waiting = new WaitForeverStage();
        var pipeline = Pipeline.Start(waiting, "in-process-cancellation").Build(waiting);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var observations = new List<PipelineObservation>();
        var observer = new InlinePipelineObserver(observation => observations.Add(observation));

        var act = () =>
            new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(0),
                observer,
                cancellation.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        observations
            .OfType<PipelineStepCancelled>()
            .Should()
            .ContainSingle(observation => observation.StepId == "runner-wait");
    }

    [Fact]
    public async Task RunAsync_HandlerFailureFaultsInteractionAndCancelsRun()
    {
        var pipeline = BuildInteractionPipeline("in-process-handler-failure");
        var handler = new InlineExternalRequestHandler(_ =>
            throw new IOException("handler failed")
        );
        var observations = new List<PipelineObservation>();
        var observer = new InlinePipelineObserver(observation => observations.Add(observation));

        var act = () =>
            new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(0),
                handler,
                observer,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<IOException>().WithMessage("handler failed");
        observations
            .OfType<PipelineStepFaulted>()
            .Should()
            .ContainSingle(observation => observation.StepId == "probe-input");
    }

    [Fact]
    public async Task PersistentInteraction_ObservesRequestAndResponseBeforeStateApply()
    {
        var start = new InteractionStartStage();
        var interaction = PipelineNodes.WaitFor<RunnerState, ProbeQuestion, ProbeAnswer>(
            "probe-input",
            state => new ProbeQuestion($"Current count: {state.Count}"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new TestCompletion<RunnerState>("complete"));
        var pipeline = Pipeline
            .Start(start, "persistent-interaction")
            .Persist()
            .Route(on: start.Success, to: interaction, label: "ask")
            .Route(when: _ => true, from: interaction, to: complete, label: "answered")
            .Build(complete);
        var observations = new List<PipelineObservation>();
        var observer = new InlinePersistenceObserver(observations.Add);
        var handler = new InlineExternalRequestHandler(request => new ExternalRequestAnswer(
            request.RunId,
            request.RequestId,
            typeof(ProbeAnswer),
            new ProbeAnswer("accepted")
        ));

        var result = await new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            new RunnerState(0),
            handler,
            observer,
            CancellationToken.None
        );

        result.State.Answer.Should().Be("accepted");
        observations
            .OfType<PipelineInteractionRequestedObservation>()
            .Should()
            .ContainSingle()
            .Which.Payload.Should()
            .NotBeNull();
        observations
            .OfType<PipelineInteractionAnsweredObservation>()
            .Should()
            .ContainSingle()
            .Which.Payload!.Value.GetProperty("text")
            .GetString()
            .Should()
            .Be("accepted");
    }

    [Fact]
    public async Task PersistentInteraction_RequestPersistenceFailurePreventsHandlerDispatch()
    {
        var pipeline = BuildPersistentInteractionPipeline(_ => { });
        var handlerCalled = false;
        var handler = new InlineExternalRequestHandler(request =>
        {
            handlerCalled = true;
            return new ExternalRequestAnswer(
                request.RunId,
                request.RequestId,
                typeof(ProbeAnswer),
                new ProbeAnswer("accepted")
            );
        });
        var observer = new FailingPersistenceObserver<PipelineInteractionRequestedObservation>();

        var run = async () =>
            await new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(0),
                handler,
                observer,
                CancellationToken.None
            );

        await run.Should().ThrowAsync<IOException>().WithMessage("persistence failed");
        handlerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task PersistentInteraction_ResponsePersistenceFailurePreventsStateApply()
    {
        var applyCount = 0;
        var pipeline = BuildPersistentInteractionPipeline(_ => applyCount++);
        var handler = new InlineExternalRequestHandler(request => new ExternalRequestAnswer(
            request.RunId,
            request.RequestId,
            typeof(ProbeAnswer),
            new ProbeAnswer("accepted")
        ));
        var observer = new FailingPersistenceObserver<PipelineInteractionAnsweredObservation>();

        var run = async () =>
            await new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(0),
                handler,
                observer,
                CancellationToken.None
            );

        await run.Should().ThrowAsync<IOException>().WithMessage("persistence failed");
        applyCount.Should().Be(0);
    }

    [Fact]
    public async Task InteractionOptOut_PreservesObservationsWithoutPayloads()
    {
        var start = new InteractionStartStage();
        var interaction = PipelineNodes.WaitFor<RunnerState, ProbeQuestion, ProbeAnswer>(
            "probe-input",
            state => new ProbeQuestion($"Current count: {state.Count}"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new TestCompletion<RunnerState>("complete"));
        var pipeline = Pipeline
            .Start(start, "interaction-opt-out")
            .Persist()
            .DoNotPersist(interaction)
            .Route(on: start.Success, to: interaction, label: "ask")
            .Route(when: _ => true, from: interaction, to: complete, label: "answered")
            .Build(complete);
        var observations = new List<PipelineObservation>();
        var handler = new InlineExternalRequestHandler(request => new ExternalRequestAnswer(
            request.RunId,
            request.RequestId,
            typeof(ProbeAnswer),
            new ProbeAnswer("accepted")
        ));

        var result = await new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            new RunnerState(0),
            handler,
            new InlinePersistenceObserver(observations.Add),
            CancellationToken.None
        );

        result.State.Answer.Should().Be("accepted");
        observations
            .OfType<PipelineInteractionRequestedObservation>()
            .Should()
            .ContainSingle()
            .Which.Payload.Should()
            .BeNull();
        observations
            .OfType<PipelineInteractionAnsweredObservation>()
            .Should()
            .ContainSingle()
            .Which.Payload.Should()
            .BeNull();
    }

    [Fact]
    public async Task RunAsync_ResumesTypedInteractionWithMatchingAnswer()
    {
        var start = new InteractionStartStage();
        var interaction = PipelineNodes.WaitFor<RunnerState, ProbeQuestion, ProbeAnswer>(
            "probe-input",
            state => new ProbeQuestion($"Current count: {state.Count}"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new TestCompletion<RunnerState>("complete"));
        var pipeline = Pipeline
            .Start(start, "in-process-interaction")
            .Route(on: start.Success, to: interaction, label: "ask")
            .Route(when: _ => true, from: interaction, to: complete, label: "answered")
            .Build(complete);
        PendingExternalRequest? observed = null;
        var handler = new InlineExternalRequestHandler(request =>
        {
            observed = request;
            return new ExternalRequestAnswer(
                request.RunId,
                request.RequestId,
                typeof(ProbeAnswer),
                new ProbeAnswer("continue")
            );
        });

        var output = await new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            new RunnerState(3),
            handler,
            CancellationToken.None
        );

        observed.Should().NotBeNull();
        observed!.PortId.Should().Be("probe-input");
        observed.RequestType.Should().Be(typeof(ProbeQuestion));
        observed.ResponseType.Should().Be(typeof(ProbeAnswer));
        observed.Value.Should().Be(new ProbeQuestion("Current count: 3"));
        output.State.Answer.Should().Be("continue");
    }

    [Fact]
    public async Task RunAsync_RejectsAnswerForAnotherRequest()
    {
        var start = new InteractionStartStage();
        var interaction = PipelineNodes.WaitFor<RunnerState, ProbeQuestion, ProbeAnswer>(
            "probe-input",
            state => new ProbeQuestion($"Current count: {state.Count}"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new TestCompletion<RunnerState>("complete"));
        var pipeline = Pipeline
            .Start(start, "in-process-wrong-answer")
            .Route(on: start.Success, to: interaction, label: "ask")
            .Route(when: _ => true, from: interaction, to: complete, label: "answered")
            .Build(complete);
        var handler = new InlineExternalRequestHandler(_ => new ExternalRequestAnswer(
            Guid.CreateVersion7(),
            "another-request",
            typeof(ProbeAnswer),
            new ProbeAnswer("continue")
        ));
        var observations = new List<PipelineObservation>();
        var observer = new InlinePipelineObserver(observation => observations.Add(observation));

        var act = () =>
            new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(3),
                handler,
                observer,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another-request*");
        observations
            .OfType<PipelineStepFaulted>()
            .Should()
            .ContainSingle(observation => observation.StepId == "probe-input");
        observations
            .OfType<PipelineStepCompleted>()
            .Should()
            .NotContain(observation => observation.StepId == "probe-input");
    }

    [Fact]
    public async Task WaitingRun_DoesNotPreventAnotherRunFromCompleting()
    {
        await using var broker = new InMemoryExternalRequestBroker();
        var waitingRun = new InProcessPipelineRunner().RunAsync(
            BuildInteractionPipeline("in-process-waiting-run"),
            Guid.CreateVersion7(),
            new RunnerState(4),
            broker,
            CancellationToken.None
        );
        var pending = await WaitForPendingAsync(broker);
        var increment = new IncrementStage();
        var independentPipeline = Pipeline
            .Start(increment, "in-process-independent-run")
            .Build(increment);

        var independentOutput = await new InProcessPipelineRunner().RunAsync(
            independentPipeline,
            Guid.CreateVersion7(),
            new RunnerState(10),
            CancellationToken.None
        );

        independentOutput.State.Count.Should().Be(11);
        waitingRun.IsCompleted.Should().BeFalse();
        broker.Answer(
            new ExternalRequestAnswer(
                pending.RunId,
                pending.RequestId,
                typeof(ProbeAnswer),
                new ProbeAnswer("resumed")
            )
        );
        var waitingOutput = await waitingRun;
        waitingOutput.State.Answer.Should().Be("resumed");
    }

    [Fact]
    public async Task ConcurrentRuns_UsingSameInteractivePipelineRemainIsolated()
    {
        var pipeline = BuildInteractionPipeline("same-pipeline-concurrent-runs");
        var firstRun = new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            new RunnerState(1),
            new InlineExternalRequestHandler(request => new ExternalRequestAnswer(
                request.RunId,
                request.RequestId,
                typeof(ProbeAnswer),
                new ProbeAnswer("first")
            )),
            CancellationToken.None
        );
        var secondRun = new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            new RunnerState(2),
            new InlineExternalRequestHandler(request => new ExternalRequestAnswer(
                request.RunId,
                request.RequestId,
                typeof(ProbeAnswer),
                new ProbeAnswer("second")
            )),
            CancellationToken.None
        );

        var outputs = await Task.WhenAll(firstRun, secondRun);

        outputs[0].State.Answer.Should().Be("first");
        outputs[1].State.Answer.Should().Be("second");
    }

    [Fact]
    public async Task CancellationWhileWaiting_ClearsPendingRequest()
    {
        var pipeline = BuildInteractionPipeline("cancel-pending-interaction");
        await using var broker = new InMemoryExternalRequestBroker();
        using var cancellation = new CancellationTokenSource();
        var run = new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            new RunnerState(1),
            broker,
            cancellation.Token
        );
        await WaitForPendingAsync(broker);

        await cancellation.CancelAsync();

        var act = async () => await run;
        await act.Should().ThrowAsync<OperationCanceledException>();
        broker.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task RequestHalt_PublishesEveryRequestBeforeAnyBranchResumes()
    {
        var runId = Guid.CreateVersion7();
        var invocationCount = 0;
        var initial = new PipelineMessage<RunnerState>(
            PipelineRuntime.Create(runId),
            new RunnerState(0)
        );
        var start = CorePipelineNodes
            .Stage<PipelineMessage<RunnerState>, PipelineMessage<RunnerState>>(
                "start",
                (message, _, _) => ValueTask.FromResult(message)
            )
            .Bind();
        var firstRequest = CorePipelineNodes
            .Stage<PipelineMessage<RunnerState>, FirstQuestion>(
                "first-request",
                (_, _, _) => ValueTask.FromResult(new FirstQuestion("first"))
            )
            .Bind();
        var secondRequest = CorePipelineNodes
            .Stage<PipelineMessage<RunnerState>, SecondQuestion>(
                "second-request",
                async (_, _, cancellationToken) =>
                {
                    await Task.Delay(50, cancellationToken);
                    return new SecondQuestion("second");
                }
            )
            .Bind();
        var firstPort = (ExecutorBinding)
            RequestPort.Create<FirstQuestion, FirstAnswer>("first-port");
        var secondPort = (ExecutorBinding)
            RequestPort.Create<SecondQuestion, SecondAnswer>("second-port");
        var firstResume = CorePipelineNodes
            .Stage<FirstAnswer, PipelineMessage<RunnerState>>(
                "first-resume",
                (_, _, _) => ResumeAfterBothRequests()
            )
            .Bind();
        var secondResume = CorePipelineNodes
            .Stage<SecondAnswer, PipelineMessage<RunnerState>>(
                "second-resume",
                (_, _, _) => ResumeAfterBothRequests()
            )
            .Bind();
        var workflow = new WorkflowBuilder(start)
            .WithName("runner-multiple-requests")
            .AddFanOutEdge(start, [firstRequest, secondRequest])
            .AddEdge(firstRequest, firstPort)
            .AddEdge(firstPort, firstResume)
            .AddEdge(secondRequest, secondPort)
            .AddEdge(secondPort, secondResume)
            .WithOutputFrom(firstResume, secondResume)
            .Build();
        var pipeline = new Pipeline<RunnerState>(
            workflow,
            ["first-resume", "second-resume"],
            [],
            []
        );
        var handler = new InlineExternalRequestHandler(request =>
        {
            Interlocked.Increment(ref invocationCount);
            return request.Value switch
            {
                FirstQuestion => new ExternalRequestAnswer(
                    runId,
                    request.RequestId,
                    typeof(FirstAnswer),
                    new FirstAnswer("first")
                ),
                SecondQuestion => new ExternalRequestAnswer(
                    runId,
                    request.RequestId,
                    typeof(SecondAnswer),
                    new SecondAnswer("second")
                ),
                _ => throw new InvalidOperationException("Unexpected request type."),
            };
        });

        await new InProcessPipelineRunner().RunAsync(
            pipeline,
            runId,
            initial.State,
            handler,
            CancellationToken.None
        );

        invocationCount.Should().Be(2);

        ValueTask<PipelineMessage<RunnerState>> ResumeAfterBothRequests()
        {
            if (Volatile.Read(ref invocationCount) != 2)
            {
                throw new InvalidOperationException(
                    "A branch resumed before all requests were published."
                );
            }
            return ValueTask.FromResult(initial);
        }
    }

    private static Pipeline<RunnerState> BuildInteractionPipeline(string name)
    {
        var start = new InteractionStartStage();
        var interaction = PipelineNodes.WaitFor<RunnerState, ProbeQuestion, ProbeAnswer>(
            "probe-input",
            state => new ProbeQuestion($"Current count: {state.Count}"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new TestCompletion<RunnerState>("complete"));
        return Pipeline
            .Start(start, name)
            .Route(on: start.Success, to: interaction, label: "ask")
            .Route(when: _ => true, from: interaction, to: complete, label: "answered")
            .Build(complete);
    }

    private static async Task<PendingExternalRequest> WaitForPendingAsync(
        InMemoryExternalRequestBroker broker
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (broker.PendingCount == 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        return broker.PendingRequests.Single();
    }

    private static Pipeline<RunnerState> BuildPersistentInteractionPipeline(
        Action<RunnerState> apply
    )
    {
        var start = new InteractionStartStage();
        var interaction = PipelineNodes.WaitFor<RunnerState, ProbeQuestion, ProbeAnswer>(
            "probe-input",
            state => new ProbeQuestion($"Current count: {state.Count}"),
            (state, answer) =>
            {
                apply(state);
                return state with { Answer = answer.Text };
            }
        );
        var complete = PipelineNodes.Complete(new TestCompletion<RunnerState>("complete"));
        return Pipeline
            .Start(start, "persistent-interaction-failure")
            .Persist()
            .Route(on: start.Success, to: interaction, label: "ask")
            .Route(when: _ => true, from: interaction, to: complete, label: "answered")
            .Build(complete);
    }

    private sealed class InlineExternalRequestHandler(
        Func<PendingExternalRequest, ExternalRequestAnswer> answer
    ) : IExternalRequestHandler
    {
        public ValueTask<ExternalRequestAnswer> WaitAsync(
            PendingExternalRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(answer(request));
    }

    private sealed class InlinePipelineObserver(Action<PipelineObservation> observe)
        : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            observe(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InlinePersistenceObserver(Action<PipelineObservation>? observe = null)
        : IPipelinePersistenceObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            observe?.Invoke(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingPersistenceObserver<TObservation> : IPipelinePersistenceObserver
        where TObservation : PipelineObservation
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        ) =>
            observation is TObservation
                ? ValueTask.FromException(new IOException("persistence failed"))
                : ValueTask.CompletedTask;
    }

    private sealed class ProbeCompletion : IPipelineCompletion<RunnerState>
    {
        public string Id => "definition-complete";
        public int ApplyCount { get; private set; }

        public string Summarize(RunnerState state) => $"Completed at {state.Count}";

        public RunnerState Complete(RunnerState state)
        {
            ApplyCount++;
            return state with { Count = state.Count + 10 };
        }
    }

    private sealed class ProbeFailure : IPipelineFailure<RunnerState>
    {
        public string Id => "definition-failed";
        public int ApplyCount { get; private set; }

        public string Summarize(RunnerState state) => $"Failed at {state.Count}";

        public RunnerState Fail(RunnerState state)
        {
            ApplyCount++;
            return state with { Count = -1 };
        }
    }
}

public sealed record RunnerState(int Count, string? Answer = null);

public record RunnerBaseState(int Count);

public sealed record RunnerDerivedState(int Count, string Detail) : RunnerBaseState(Count);

public sealed record ProbeQuestion(string Text);

public sealed record ProbeAnswer(string Text);

internal sealed record FirstQuestion(string Text);

internal sealed record FirstAnswer(string Text);

internal sealed record SecondQuestion(string Text);

internal sealed record SecondAnswer(string Text);

internal sealed class ProbeException : Exception;

[PipelineStage("runner-increment")]
public sealed partial class IncrementStage
{
    public ValueTask<RunnerState> ExecuteAsync(RunnerState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Count = state.Count + 1 });
}

[PipelineStage("runner-successful-outcome")]
public sealed partial class SuccessfulOutcomeStage
{
    public ValueTask<Outcome<RunnerState>> ExecuteAsync(RunnerState state, CancellationToken _) =>
        ValueTask.FromResult<Outcome<RunnerState>>(
            new Outcome<RunnerState>.Success(state with { Count = state.Count + 2 })
        );
}

[PipelineStage("runner-polymorphic-state")]
public sealed partial class PolymorphicStateStage
{
    public ValueTask<RunnerBaseState> ExecuteAsync(RunnerBaseState state, CancellationToken _) =>
        ValueTask.FromResult<RunnerBaseState>(new RunnerDerivedState(state.Count + 1, "persisted"));
}

[PipelineStage("runner-fault")]
public sealed partial class FaultStage
{
    public ValueTask<RunnerState> ExecuteAsync(RunnerState _, CancellationToken __) =>
        throw new ProbeException();
}

[PipelineStage("runner-declared-failure")]
public sealed partial class DeclaredFailureStage
{
    public ValueTask<Outcome<RunnerState>> ExecuteAsync(RunnerState state, CancellationToken _) =>
        ValueTask.FromResult<Outcome<RunnerState>>(
            new Outcome<RunnerState>.Failed(
                state,
                new FailureEvidence("runner.expected", "Expected failure")
            )
        );
}

[PipelineStage("runner-wait")]
public sealed partial class WaitForeverStage
{
    public async ValueTask ExecuteAsync(RunnerState _, CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}

[PipelineStage("runner-interaction-start")]
public sealed partial class InteractionStartStage
{
    public ValueTask<Outcome<RunnerState>> ExecuteAsync(RunnerState state, CancellationToken _) =>
        ValueTask.FromResult<Outcome<RunnerState>>(new Outcome<RunnerState>.Success(state));
}
