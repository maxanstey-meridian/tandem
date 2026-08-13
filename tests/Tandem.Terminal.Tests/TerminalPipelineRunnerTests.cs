using FluentAssertions;
using Spectre.Console.Testing;

namespace Tandem.Terminal.Tests;

public sealed class TerminalPipelineRunnerTests
{
    [Fact]
    public async Task RunOptionsOwnInteractionsWithoutDuplicateTerminalConfiguration()
    {
        var interaction = PipelineNodes.WaitFor<State, int, int>(
            "answer",
            state => state.Value,
            (state, answer) => state with { Value = answer }
        );
        var done = PipelineNodes.Complete(new Complete());
        var pipeline = Pipeline
            .Start(interaction, "interaction-run")
            .Route(interaction, done, "answered")
            .Build(done);
        var handlers = new PipelineInteractionHandlers().Handle(
            interaction,
            (request, _) => ValueTask.FromResult(request.Request + 1)
        );

        var result = await new PipelineRunner().RunWithTerminalAsync(
            pipeline,
            new State(1),
            new TerminalPipelineRunOptions
            {
                Run = new PipelineRunOptions(Interactions: handlers),
                Display = PlainDisplay(),
            }
        );

        result.State.Value.Should().Be(2);
    }

    [Fact]
    public async Task InteractiveQuitCancelsSharedRunTokenAndComposesConfiguredCancellation()
    {
        var waiting = PipelineNodes.Stage<State>(
            "wait",
            async (state, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return state;
            }
        );
        var pipeline = Pipeline.Start(waiting, "cancel-run").Build(waiting);
        using var runCancellation = new CancellationTokenSource();
        var configuredCancellation = false;

        var run = () =>
            new PipelineRunner().RunWithTerminalAsync(
                pipeline,
                new State(1),
                new TerminalPipelineRunOptions
                {
                    RunCancellation = runCancellation,
                    Display = InteractiveDisplay(
                        new QueueInput(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)),
                        _ =>
                        {
                            configuredCancellation = true;
                            return ValueTask.CompletedTask;
                        }
                    ),
                }
            );

        await run.Should().ThrowAsync<OperationCanceledException>();
        runCancellation.IsCancellationRequested.Should().BeTrue();
        configuredCancellation.Should().BeTrue();
    }

    [Fact]
    public async Task TerminalizesBeforeInteractiveDisplayWaitsForDismissal()
    {
        var complete = PipelineNodes.Stage<State>(
            "complete",
            (state, _) => ValueTask.FromResult(state)
        );
        var pipeline = Pipeline.Start(complete, "terminalize-run").Build(complete);
        var input = new QueueInput();
        var terminalized = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var run = new PipelineRunner().RunWithTerminalAsync(
            pipeline,
            new State(1),
            new TerminalPipelineRunOptions
            {
                Display = InteractiveDisplay(input),
                TerminalizingAsync = (completion, _) =>
                {
                    completion.Status.Should().Be(TerminalPipelineStatus.Succeeded);
                    terminalized.TrySetResult();
                    return ValueTask.CompletedTask;
                },
            }
        );

        await terminalized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.IsCompleted.Should().BeFalse();
        input.Enqueue(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false));
        (await run.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded.Should().BeTrue();
    }

    private static TerminalDisplayOptions PlainDisplay() =>
        new()
        {
            Console = new TestConsole(),
            Capabilities = new TerminalCapabilities(false, false),
        };

    private static TerminalDisplayOptions InteractiveDisplay(
        QueueInput input,
        Func<CancellationToken, ValueTask>? cancel = null
    ) =>
        new()
        {
            Console = new TestConsole().Width(80).Height(20),
            Capabilities = new TerminalCapabilities(true, true),
            KeyInput = input,
            CancelAsync = cancel,
            RefreshInterval = TimeSpan.FromMilliseconds(10),
        };

    private sealed record State(int Value);

    private sealed class Complete : IPipelineCompletion<State>
    {
        public string Id => "done";

        public string Summarize(State state) => "Done";
    }

    private sealed class QueueInput(params ConsoleKeyInfo[] keys) : ITerminalKeyInput
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public void Enqueue(ConsoleKeyInfo key)
        {
            lock (_keys)
            {
                _keys.Enqueue(key);
            }
        }

        public ValueTask<ConsoleKeyInfo?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_keys)
            {
                return ValueTask.FromResult(
                    _keys.TryDequeue(out var key) ? (ConsoleKeyInfo?)key : null
                );
            }
        }
    }
}
