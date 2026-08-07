using System.Text.Json;
using FluentAssertions;
using Tandem.Domain;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class InProcessPipelineRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsTypedPipelineOutput()
    {
        var increment = new IncrementStage();
        var pipeline = TandemWorkflow.Start(increment, "in-process-completion").Build(increment);
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
    public async Task RunAsync_ReturnsDeclaredFailureAsOutput()
    {
        var failure = new DeclaredFailureStage();
        var pipeline = TandemWorkflow.Start(failure, "in-process-declared-failure").Build(failure);

        var output = await new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            new RunnerState(1),
            CancellationToken.None
        );

        output.Disposition.Should().Be(PipelineRunDisposition.Failed);
        output.LatestResult!.CaseId.Should().Be(nameof(Outcome<RunnerState>.Failed));
    }

    [Fact]
    public async Task RunAsync_PropagatesExecutionFault()
    {
        var fault = new FaultStage();
        var pipeline = TandemWorkflow.Start(fault, "in-process-fault").Build(fault);

        var act = () =>
            new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(0),
                CancellationToken.None
            );

        var exception = await act.Should().ThrowAsync<WorkflowRunException>();
        exception.Which.InnerException.Should().BeOfType<ProbeException>();
    }

    [Fact]
    public async Task RunAsync_CancelsTheLiveWorkflow()
    {
        var waiting = new WaitForeverStage();
        var pipeline = TandemWorkflow.Start(waiting, "in-process-cancellation").Build(waiting);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () =>
            new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(0),
                cancellation.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
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
        var complete = PipelineNodes.Complete<RunnerState>("complete");
        var pipeline = TandemWorkflow
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
                JsonSerializer.SerializeToElement(new ProbeAnswer("continue"))
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
        observed.RequestType.Should().Be(typeof(ProbeQuestion).FullName);
        observed.ResponseType.Should().Be(typeof(ProbeAnswer).FullName);
        observed.Payload.Deserialize<ProbeQuestion>()!.Text.Should().Be("Current count: 3");
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
        var complete = PipelineNodes.Complete<RunnerState>("complete");
        var pipeline = TandemWorkflow
            .Start(start, "in-process-wrong-answer")
            .Route(on: start.Success, to: interaction, label: "ask")
            .Route(when: _ => true, from: interaction, to: complete, label: "answered")
            .Build(complete);
        var handler = new InlineExternalRequestHandler(_ => new ExternalRequestAnswer(
            Guid.CreateVersion7(),
            "another-request",
            JsonSerializer.SerializeToElement(new ProbeAnswer("continue"))
        ));

        var act = () =>
            new InProcessPipelineRunner().RunAsync(
                pipeline,
                Guid.CreateVersion7(),
                new RunnerState(3),
                handler,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another-request*");
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
        var independentPipeline = TandemWorkflow
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
                JsonSerializer.SerializeToElement(new ProbeAnswer("resumed"))
            )
        );
        var waitingOutput = await waitingRun;
        waitingOutput.State.Answer.Should().Be("resumed");
    }

    private static Pipeline BuildInteractionPipeline(string name)
    {
        var start = new InteractionStartStage();
        var interaction = PipelineNodes.WaitFor<RunnerState, ProbeQuestion, ProbeAnswer>(
            "probe-input",
            state => new ProbeQuestion($"Current count: {state.Count}"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete<RunnerState>("complete");
        return TandemWorkflow
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

    private sealed class InlineExternalRequestHandler(
        Func<PendingExternalRequest, ExternalRequestAnswer> answer
    ) : IExternalRequestHandler
    {
        public ValueTask<ExternalRequestAnswer> WaitAsync(
            PendingExternalRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(answer(request));
    }
}

public sealed record RunnerState(int Count, string? Answer = null);

public sealed record ProbeQuestion(string Text);

public sealed record ProbeAnswer(string Text);

internal sealed class ProbeException : Exception;

[PipelineStage("runner-increment")]
public sealed partial class IncrementStage
{
    public ValueTask<RunnerState> ExecuteAsync(RunnerState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Count = state.Count + 1 });
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
