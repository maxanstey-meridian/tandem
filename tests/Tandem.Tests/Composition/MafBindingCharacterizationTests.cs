using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Tandem.Infrastructure.Projection;

namespace Tandem.Tests.Composition;

public sealed class MafBindingCharacterizationTests
{
    [Fact]
    public void SameExecutor_BindExecutorTwice_CreatesFreshBindingsWithStableIdentity()
    {
        var executor = new PlainStepExecutor<int, int>(
            "increment",
            new PlainStep<int, int>(value => value + 1)
        );

        var first = executor.BindExecutor();
        var second = executor.BindExecutor();

        first.Should().NotBeSameAs(second);
        first.Id.Should().Be("increment");
        second.Id.Should().Be("increment");
        first.ExecutorType.Should().Be(second.ExecutorType);
        first.IsSharedInstance.Should().Be(second.IsSharedInstance);
        first
            .SupportsConcurrentSharedExecution.Should()
            .Be(second.SupportsConcurrentSharedExecution);
        first.SupportsResetting.Should().Be(second.SupportsResetting);
    }

    [Fact]
    public async Task FreshBindingsFromSameExecutor_BuildAndRunIndependentWorkflows()
    {
        var invocations = 0;
        var executor = new PlainStepExecutor<int, int>(
            "increment",
            new PlainStep<int, int>(value =>
            {
                Interlocked.Increment(ref invocations);
                return value + 1;
            })
        );

        var first = BuildSingleStep(executor.BindExecutor(), "fresh-binding-one");
        var second = BuildSingleStep(executor.BindExecutor(), "fresh-binding-two");

        (await RunAsync<int, int>(first, 1, "fresh-binding-one")).Should().Be(2);
        (await RunAsync<int, int>(second, 10, "fresh-binding-two")).Should().Be(11);
        invocations.Should().Be(2);
        first.ReflectExecutors().Keys.Should().Equal("increment");
        second.ReflectExecutors().Keys.Should().Equal("increment");
    }

    [Fact]
    public async Task SameBinding_CanBuildAndRunTwoSequentialWorkflows()
    {
        var executor = new PlainStepExecutor<int, int>(
            "increment",
            new PlainStep<int, int>(value => value + 1)
        );
        var binding = executor.BindExecutor();
        var first = BuildSingleStep(binding, "shared-binding-one");
        var second = BuildSingleStep(binding, "shared-binding-two");

        (await RunAsync<int, int>(first, 1, "shared-binding-one")).Should().Be(2);
        (await RunAsync<int, int>(second, 10, "shared-binding-two")).Should().Be(11);
    }

    [Fact]
    public async Task FreshBindings_SupportConcurrentIndependentBuildsAndRuns()
    {
        var observations = new ConcurrentBag<(string Id, int Output)>();

        await Task.WhenAll(
            Enumerable
                .Range(0, 32)
                .Select(async index =>
                {
                    var id = $"increment-{index}";
                    var executor = new PlainStepExecutor<int, int>(
                        id,
                        new PlainStep<int, int>(value => value + 1)
                    );
                    var workflow = BuildSingleStep(
                        executor.BindExecutor(),
                        $"concurrent-build-{index}"
                    );
                    var output = await RunAsync<int, int>(
                        workflow,
                        index,
                        $"concurrent-run-{index}"
                    );
                    observations.Add((workflow.ReflectExecutors().Keys.Single(), output));
                })
        );

        observations.Should().HaveCount(32);
        observations.Select(item => item.Id).Should().OnlyHaveUniqueItems();
        observations.Select(item => item.Output).Should().BeEquivalentTo(Enumerable.Range(1, 32));
    }

    [Fact]
    public async Task PlainStepAdapter_PreservesTypingReflectionAndObservation()
    {
        var step = new PlainStep<ProbeMessage, ProbeMessage>(message =>
            message with
            {
                Count = message.Count + 1,
            }
        );
        typeof(PlainStep<ProbeMessage, ProbeMessage>)
            .GetInterfaces()
            .Any(type => type.Namespace?.StartsWith("Microsoft.Agents.AI") == true)
            .Should()
            .BeFalse();
        typeof(PlainStep<ProbeMessage, ProbeMessage>).BaseType.Should().Be(typeof(object));

        var observer = new RecordingObserver();
        var adapter = new PlainStepExecutor<ProbeMessage, ProbeMessage>("plain-step", step);
        var observed = new ObservedExecutor<ProbeMessage, ProbeMessage>(
            "plain-step",
            adapter,
            observer
        );
        var workflow = BuildSingleStep(observed.BindExecutor(), "plain-step-observed");

        var output = await RunAsync<ProbeMessage, ProbeMessage>(
            workflow,
            new ProbeMessage(0),
            "plain-step-observed"
        );

        output.Count.Should().Be(1);
        workflow.ReflectExecutors().Keys.Should().Equal("plain-step");
        observer.Started.Should().Equal("plain-step");
        observer.Completed.Should().Equal("plain-step");
    }

    [Fact]
    public void PlainStepAdapter_ComposesWithTypedRequestPort()
    {
        var request = new PlainStepExecutor<ProbeMessage, HumanQuestion>(
            "request",
            new PlainStep<ProbeMessage, HumanQuestion>(message => new HumanQuestion(
                message.Count.ToString()
            ))
        ).BindExecutor();
        var port = (ExecutorBinding)RequestPort.Create<HumanQuestion, HumanAnswer>("human-input");
        var apply = new PlainStepExecutor<HumanAnswer, ProbeMessage>(
            "apply",
            new PlainStep<HumanAnswer, ProbeMessage>(answer => new ProbeMessage(
                answer.Value.Length
            ))
        ).BindExecutor();

        var workflow = new WorkflowBuilder(request)
            .WithName("plain-step-port")
            .AddEdge(request, port)
            .AddEdge(port, apply)
            .WithOutputFrom(apply)
            .Build();

        workflow.ReflectExecutors().Keys.Should().BeEquivalentTo("request", "human-input", "apply");
        var reflectedPort = workflow.ReflectPorts().Values.Should().ContainSingle().Subject;
        reflectedPort.PortId.Should().Be("human-input");
        reflectedPort.RequestType.TypeName.Should().Be(typeof(HumanQuestion).FullName);
        reflectedPort.ResponseType.TypeName.Should().Be(typeof(HumanAnswer).FullName);
    }

    private static Workflow BuildSingleStep(ExecutorBinding binding, string name) =>
        new WorkflowBuilder(binding).WithName(name).WithOutputFrom(binding).Build();

    private static async Task<TOutput> RunAsync<TInput, TOutput>(
        Workflow workflow,
        TInput input,
        string runId
    )
        where TInput : notnull
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            input,
            runId,
            CancellationToken.None
        );
        TOutput? output = default;
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
            else if (evt is WorkflowOutputEvent workflowOutput && workflowOutput.Is<TOutput>())
            {
                output = workflowOutput.As<TOutput>();
            }
        }

        failure.Should().BeNull();
        output.Should().NotBeNull();
        return output!;
    }

    private sealed class PlainStep<TInput, TOutput>(Func<TInput, TOutput> execute)
    {
        public ValueTask<TOutput> ExecuteAsync(TInput input, CancellationToken _) =>
            ValueTask.FromResult(execute(input));
    }

    private sealed class PlainStepExecutor<TInput, TOutput>(
        string id,
        PlainStep<TInput, TOutput> step
    ) : Executor<TInput, TOutput>(id)
    {
        public override ValueTask<TOutput> HandleAsync(
            TInput input,
            IWorkflowContext context,
            CancellationToken cancellationToken
        ) => step.ExecuteAsync(input, cancellationToken);
    }

    private sealed class RecordingObserver : IBlockExecutionObserver
    {
        public List<string> Started { get; } = [];
        public List<string> Completed { get; } = [];

        public ValueTask StartedAsync(string blockId, CancellationToken cancellationToken)
        {
            Started.Add(blockId);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompletedAsync<TInput, TOutput>(
            string blockId,
            TInput input,
            TOutput output,
            TimeSpan duration,
            CancellationToken cancellationToken
        )
        {
            Completed.Add(blockId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ProbeMessage(int Count);

    private sealed record HumanQuestion(string Value);

    private sealed record HumanAnswer(string Value);
}
