using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Workflows;

#pragma warning disable MAAI001

namespace Tandem.Tests.Composition;

public sealed class MafBindingCharacterizationTests
{
    [Fact]
    public void HarnessFileTools_HaveStableConstantsAndCompleteReadWriteClassification()
    {
        var reads = new[]
        {
            FileAccessProvider.ReadFileToolName,
            FileAccessProvider.LsToolName,
            FileAccessProvider.GrepToolName,
        };
        var mutations = new[]
        {
            FileAccessProvider.WriteToolName,
            FileAccessProvider.DeleteFileToolName,
            FileAccessProvider.ReplaceToolName,
            FileAccessProvider.ReplaceLinesToolName,
        };

        reads.Should().Equal("file_access_read", "file_access_ls", "file_access_grep");
        mutations
            .Should()
            .Equal(
                "file_access_write",
                "file_access_delete",
                "file_access_replace",
                "file_access_replace_lines"
            );
        reads.Concat(mutations).Should().OnlyHaveUniqueItems().And.HaveCount(7);
    }

    [Fact]
    public void ContextWindowCompaction_UsesAnInputBudgetAndOrderedThresholds()
    {
        var strategy = new ContextWindowCompactionStrategy(
            maxContextWindowTokens: 100_000,
            maxOutputTokens: 10_000,
            toolEvictionThreshold: 0.5,
            truncationThreshold: 0.8
        );

        strategy.InputBudgetTokens.Should().Be(90_000);
        strategy.ToolEvictionThreshold.Should().Be(0.5);
        strategy.TruncationThreshold.Should().Be(0.8);
        new CompactionProvider(strategy).StateKeys.Should().ContainSingle();
    }

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
    public async Task PlainStepAdapter_PreservesTypingAndReflection()
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

        var adapter = new PlainStepExecutor<ProbeMessage, ProbeMessage>("plain-step", step);
        var workflow = BuildSingleStep(adapter.BindExecutor(), "plain-step-observed");

        var output = await RunAsync<ProbeMessage, ProbeMessage>(
            workflow,
            new ProbeMessage(0),
            "plain-step-observed"
        );

        output.Count.Should().Be(1);
        workflow.ReflectExecutors().Keys.Should().Equal("plain-step");
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

    [Fact]
    public async Task ConditionalEdges_FanOutToEveryMatchingDestination()
    {
        var start = new PlainStepExecutor<int, int>(
            "start",
            new PlainStep<int, int>(value => value)
        ).BindExecutor();
        var first = new PlainStepExecutor<int, string>(
            "first",
            new PlainStep<int, string>(_ => "first")
        ).BindExecutor();
        var second = new PlainStepExecutor<int, string>(
            "second",
            new PlainStep<int, string>(_ => "second")
        ).BindExecutor();
        var workflow = new WorkflowBuilder(start)
            .WithName("ordered-conditional-routing")
            .AddEdge<int>(start, first, _ => true, "first", idempotent: false)
            .AddEdge<int>(start, second, _ => true, "second", idempotent: false)
            .WithOutputFrom(first, second)
            .Build();

        var outputs = await RunAllAsync<int, string>(workflow, 1, "ordered-conditional-routing");

        outputs.Should().Equal("first", "second");
    }

    [Fact]
    public async Task MatchingConditionalEdgesToTheSameTarget_ExecuteTheTargetForEachEdge()
    {
        var invocationCount = 0;
        var start = new PlainStepExecutor<int, int>(
            "start",
            new PlainStep<int, int>(value => value)
        ).BindExecutor();
        var target = new PlainStepExecutor<int, string>(
            "target",
            new PlainStep<int, string>(_ =>
            {
                Interlocked.Increment(ref invocationCount);
                return "target";
            })
        ).BindExecutor();
        var workflow = new WorkflowBuilder(start)
            .WithName("same-target-conditional-routing")
            .AddEdge<int>(start, target, _ => true, "first", idempotent: false)
            .AddEdge<int>(start, target, _ => true, "second", idempotent: false)
            .WithOutputFrom(target)
            .Build();

        var outputs = await RunAllAsync<int, string>(
            workflow,
            1,
            "same-target-conditional-routing"
        );

        outputs.Should().Equal("target", "target");
        invocationCount.Should().Be(2);
    }

    [Fact]
    public async Task FanOut_BroadcastsTheSamePayloadReferenceAndRunsTargetsConcurrently()
    {
        var input = new ReferenceProbe();
        var entered = 0;
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var start = new PlainStepExecutor<ReferenceProbe, ReferenceProbe>(
            "start",
            new PlainStep<ReferenceProbe, ReferenceProbe>(value => value)
        ).BindExecutor();
        var first = new ConcurrentReferenceExecutor(
            "first",
            input,
            () =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.SetResult();
                }
                return bothEntered.Task;
            }
        ).BindExecutor();
        var second = new ConcurrentReferenceExecutor(
            "second",
            input,
            () =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.SetResult();
                }
                return bothEntered.Task;
            }
        ).BindExecutor();
        var workflow = new WorkflowBuilder(start)
            .WithName("fan-out-reference")
            .AddFanOutEdge(start, [first, second])
            .WithOutputFrom(first, second)
            .Build();

        var outputs = await RunAllAsync<ReferenceProbe, bool>(workflow, input, "fan-out-reference");

        outputs.Should().Equal(true, true);
    }

    [Fact]
    public async Task FanInBarrier_ReleasesSeparateMessagesAsOneDeliveryBatch()
    {
        var start = new PlainStepExecutor<int, int>(
            "start",
            new PlainStep<int, int>(value => value)
        ).BindExecutor();
        var first = new PlainStepExecutor<int, string>(
            "first",
            new PlainStep<int, string>(_ => "first")
        ).BindExecutor();
        var second = new PlainStepExecutor<int, string>(
            "second",
            new PlainStep<int, string>(_ => "second")
        ).BindExecutor();
        var collector = new BatchCollector("collector").BindExecutor();
        var workflow = new WorkflowBuilder(start)
            .WithName("fan-in-batch")
            .AddFanOutEdge(start, [first, second])
            .AddFanInBarrierEdge([first, second], collector)
            .WithOutputFrom(collector)
            .Build();

        var output = await RunAsync<int, string[]>(workflow, 1, "fan-in-batch");

        output.Should().BeEquivalentTo("first", "second");
    }

    [Fact]
    public void DuplicateUnconditionalEdges_AreAcceptedDuringConstruction()
    {
        var start = new PlainStepExecutor<int, int>(
            "start",
            new PlainStep<int, int>(value => value)
        ).BindExecutor();
        var target = new PlainStepExecutor<int, int>(
            "target",
            new PlainStep<int, int>(value => value)
        ).BindExecutor();
        var builder = new WorkflowBuilder(start).AddEdge(start, target, "first", idempotent: false);

        var act = () => builder.AddEdge(start, target, "second", idempotent: false);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SwitchCases_SelectTheFirstMatchingDestinationInDeclarationOrder()
    {
        var start = new PlainStepExecutor<int, int>(
            "start",
            new PlainStep<int, int>(value => value)
        ).BindExecutor();
        var first = new PlainStepExecutor<int, string>(
            "first",
            new PlainStep<int, string>(_ => "first")
        ).BindExecutor();
        var second = new PlainStepExecutor<int, string>(
            "second",
            new PlainStep<int, string>(_ => "second")
        ).BindExecutor();
        var workflow = new WorkflowBuilder(start)
            .WithName("ordered-switch-routing")
            .AddSwitch(
                start,
                switchBuilder =>
                    switchBuilder.AddCase<int>(_ => true, [first]).AddCase<int>(_ => true, [second])
            )
            .WithOutputFrom(first, second)
            .Build();

        var outputs = await RunAllAsync<int, string>(workflow, 1, "ordered-switch-routing");

        outputs.Should().Equal("first");
    }

    [Fact]
    public async Task RequestHalt_ExposesEveryRequestAndAcceptsConcurrentOutOfOrderResponses()
    {
        var start = new PlainStepExecutor<int, int>(
            "start",
            new PlainStep<int, int>(value => value)
        ).BindExecutor();
        var firstRequest = new PlainStepExecutor<int, FirstQuestion>(
            "first-request",
            new PlainStep<int, FirstQuestion>(value => new FirstQuestion(value))
        ).BindExecutor();
        var secondRequest = new PlainStepExecutor<int, SecondQuestion>(
            "second-request",
            new PlainStep<int, SecondQuestion>(value => new SecondQuestion(value))
        ).BindExecutor();
        var firstPort = (ExecutorBinding)
            RequestPort.Create<FirstQuestion, FirstAnswer>("first-port");
        var secondPort = (ExecutorBinding)
            RequestPort.Create<SecondQuestion, SecondAnswer>("second-port");
        var firstResume = new PlainStepExecutor<FirstAnswer, string>(
            "first-resume",
            new PlainStep<FirstAnswer, string>(answer => answer.Value)
        ).BindExecutor();
        var secondResume = new PlainStepExecutor<SecondAnswer, string>(
            "second-resume",
            new PlainStep<SecondAnswer, string>(answer => answer.Value)
        ).BindExecutor();
        var workflow = new WorkflowBuilder(start)
            .WithName("multiple-requests")
            .AddFanOutEdge(start, [firstRequest, secondRequest])
            .AddEdge(firstRequest, firstPort)
            .AddEdge(firstPort, firstResume)
            .AddEdge(secondRequest, secondPort)
            .AddEdge(secondPort, secondResume)
            .WithOutputFrom(firstResume, secondResume)
            .Build();
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            1,
            "multiple-requests",
            CancellationToken.None
        );
        var requests = new ConcurrentDictionary<string, ExternalRequest>(StringComparer.Ordinal);
        var outputs = new ConcurrentBag<string>();
        var watch = Task.Run(async () =>
        {
            await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
            {
                if (evt is RequestInfoEvent request)
                {
                    requests.TryAdd(request.Request.PortInfo.PortId, request.Request);
                }
                else if (evt is WorkflowOutputEvent output && output.Is<string>())
                {
                    outputs.Add(output.As<string>()!);
                }
            }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (requests.Count < 2)
        {
            await Task.Delay(10, timeout.Token);
        }

        await Task.WhenAll(
            run.SendResponseAsync(
                    requests["second-port"].CreateResponse(new SecondAnswer("second"))
                )
                .AsTask(),
            run.SendResponseAsync(requests["first-port"].CreateResponse(new FirstAnswer("first")))
                .AsTask()
        );
        await watch.WaitAsync(timeout.Token);

        outputs.Should().BeEquivalentTo("first", "second");
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

    private static async Task<IReadOnlyList<TOutput>> RunAllAsync<TInput, TOutput>(
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
        var outputs = new List<TOutput>();

        await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowErrorEvent error)
            {
                throw error.Exception ?? new InvalidOperationException("Workflow failed.");
            }
            if (evt is ExecutorFailedEvent failed)
            {
                throw failed.Data ?? new InvalidOperationException("Executor failed.");
            }
            if (evt is WorkflowOutputEvent workflowOutput && workflowOutput.Is<TOutput>())
            {
                var output = workflowOutput.As<TOutput>();
                if (output is null)
                {
                    throw new InvalidOperationException("Workflow produced a null output.");
                }
                outputs.Add(output);
            }
        }

        return outputs;
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

    private sealed class BatchCollector(string id) : Executor<string, string[]?>(id)
    {
        private const string ValuesKey = "values";

        protected override ValueTask OnMessageDeliveryStartingAsync(
            IWorkflowContext context,
            CancellationToken cancellationToken = default
        ) => context.QueueStateUpdateAsync(ValuesKey, new List<string>(), cancellationToken);

        public override async ValueTask<string[]?> HandleAsync(
            string message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default
        )
        {
            var values = await context.ReadOrInitStateAsync(
                ValuesKey,
                static () => new List<string>(),
                cancellationToken
            );
            values.Add(message);
            await context.QueueStateUpdateAsync(ValuesKey, values, cancellationToken);
            return null;
        }

        protected override async ValueTask OnMessageDeliveryFinishedAsync(
            IWorkflowContext context,
            CancellationToken cancellationToken = default
        )
        {
            var values = await context.ReadStateAsync<List<string>>(ValuesKey, cancellationToken);
            await context.YieldOutputAsync(values?.ToArray() ?? [], cancellationToken);
        }
    }

    private sealed class ConcurrentReferenceExecutor(
        string id,
        ReferenceProbe expected,
        Func<Task> waitForBoth
    ) : Executor<ReferenceProbe, bool>(id)
    {
        public override async ValueTask<bool> HandleAsync(
            ReferenceProbe message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default
        )
        {
            await waitForBoth().WaitAsync(cancellationToken);
            return ReferenceEquals(message, expected);
        }
    }

    private sealed record ProbeMessage(int Count);

    private sealed class ReferenceProbe;

    private sealed record HumanQuestion(string Value);

    private sealed record HumanAnswer(string Value);

    private sealed record FirstQuestion(int Value);

    private sealed record FirstAnswer(string Value);

    private sealed record SecondQuestion(int Value);

    private sealed record SecondAnswer(string Value);
}

#pragma warning restore MAAI001
