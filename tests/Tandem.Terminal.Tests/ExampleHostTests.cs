using FluentAssertions;
using Spectre.Console.Testing;
using Tandem.Examples.Hosting;
using Tandem.Ledger;

namespace Tandem.Terminal.Tests;

public sealed class ExampleHostTests
{
    [Fact]
    public async Task NonpersistentRun_ObservesPipelineAndPrintsSemanticResultOnce()
    {
        var console = new TestConsole();
        var output = new StringWriter();
        var formatCalls = 0;
        var run = SuccessfulRun(result =>
        {
            formatCalls++;
            return $"Value: {result.State.Value}";
        });

        var exitCode = await RunAsync(run, console, output);

        exitCode.Should().Be(0);
        formatCalls.Should().Be(1);
        output.ToString().Should().Contain("Status: Succeeded").And.Contain("Value: 2");
        console
            .Output.Should()
            .Contain("increment started")
            .And.Contain("increment tandem.success");
    }

    [Fact]
    public async Task PersistentRun_UsesRealSqliteObserverAndTerminalizesRun()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-host-{Guid.NewGuid():N}.sqlite3");
        var output = new StringWriter();
        try
        {
            var increment = Increment();
            var pipeline = Pipeline
                .Start(increment, "persistent-example")
                .Persist()
                .Build(increment);
            var run = new ExampleRun<TestState>(
                pipeline,
                new TestState(1),
                result => $"Value: {result.State.Value}",
                path
            );

            var exitCode = await RunAsync(run, new TestConsole(), output);

            exitCode.Should().Be(0);
            var runId = Guid.ParseExact(
                output
                    .ToString()
                    .Split(Environment.NewLine)
                    .Single(line => line.StartsWith("Run: ", StringComparison.Ordinal))[5..],
                "N"
            );
            var ledger = new SqliteLedgerStore(path);
            (await ledger.GetRunAsync(runId)).Status.Should().Be(LedgerRunStatus.Ready);
            (await ledger.ReadLatestAcceptedAsync<TestState>(runId, "increment"))
                .Should()
                .NotBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DeclaredFailure_ReturnsOneAndFormatsResultOnce()
    {
        var increment = Increment();
        var failed = PipelineNodes.Failed(new TestFailure());
        var pipeline = Pipeline
            .Start(increment, "failed-example")
            .Route(when: _ => true, from: increment, to: failed, label: "fail")
            .Build(failed);
        var formatCalls = 0;
        var run = new ExampleRun<TestState>(
            pipeline,
            new TestState(1),
            _ =>
            {
                formatCalls++;
                return "Declared failure";
            }
        );

        var exitCode = await RunAsync(run, new TestConsole(), new StringWriter());

        exitCode.Should().Be(1);
        formatCalls.Should().Be(1);
    }

    [Fact]
    public async Task RedirectedRun_IsAnsiFree()
    {
        var console = new TestConsole();
        var output = new StringWriter();

        await RunAsync(SuccessfulRun(_ => "Complete"), console, output);

        console.Output.Should().NotContain("\u001b[");
        output.ToString().Should().NotContain("\u001b[");
    }

    [Fact]
    public async Task ExecutionFault_ReturnsTwoWithoutFormattingResult()
    {
        var fault = PipelineNodes.Stage<TestState>(
            "fault",
            (_, _) => throw new InvalidOperationException("broken")
        );
        var pipeline = Pipeline.Start(fault, "fault-example").Build(fault);
        var error = new StringWriter();
        var formatCalls = 0;

        var exitCode = await ExampleHost.RunPipelineAsync(
            new ExampleRun<TestState>(
                pipeline,
                new TestState(1),
                _ =>
                {
                    formatCalls++;
                    return "Should not print";
                }
            ),
            new TerminalCapabilities(false, false),
            new TestConsole(),
            keyInput: null,
            new StringWriter(),
            error
        );

        exitCode.Should().Be(2);
        formatCalls.Should().Be(0);
        error.ToString().Should().Contain("Run faulted:");
    }

    [Fact]
    public async Task InteractiveQuit_CancelsRunnerCleansDisplayAndReturnsTwo()
    {
        var waiting = PipelineNodes.Stage<TestState>(
            "wait",
            async (state, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return state;
            }
        );
        var pipeline = Pipeline.Start(waiting, "cancel-example").Build(waiting);
        var console = new TestConsole().Width(80).Height(20);
        var output = new StringWriter();
        var error = new StringWriter();
        var formatCalls = 0;

        var exitCode = await ExampleHost
            .RunPipelineAsync(
                new ExampleRun<TestState>(
                    pipeline,
                    new TestState(1),
                    _ =>
                    {
                        formatCalls++;
                        return "Should not print";
                    }
                ),
                new TerminalCapabilities(true, true),
                console,
                new QueueInput(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)),
                output,
                error
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        exitCode.Should().Be(2);
        formatCalls.Should().Be(0);
        error.ToString().Should().Contain("cancelled");
    }

    private static ExampleRun<TestState> SuccessfulRun(
        Func<PipelineRunResult<TestState>, string> formatter
    )
    {
        var increment = Increment();
        return new(
            Pipeline.Start(increment, "successful-example").Build(increment),
            new TestState(1),
            formatter
        );
    }

    private static IGeneratedPipelineStep<TestState, GeneratedStepCompletion> Increment() =>
        PipelineNodes.Stage<TestState>(
            "increment",
            (state, _) => ValueTask.FromResult(state with { Value = state.Value + 1 })
        );

    private static Task<int> RunAsync(
        ExampleRun<TestState> run,
        TestConsole console,
        StringWriter output
    ) =>
        ExampleHost.RunPipelineAsync(
            run,
            new TerminalCapabilities(false, false),
            console,
            keyInput: null,
            output,
            new StringWriter()
        );

    private sealed record TestState(int Value);

    private sealed class TestFailure : IPipelineFailure<TestState>
    {
        public string Id => "failed";

        public string Summarize(TestState state) => "Failed as declared";
    }

    private sealed class QueueInput(params ConsoleKeyInfo[] keys) : ITerminalKeyInput
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public ValueTask<ConsoleKeyInfo?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _keys.TryDequeue(out var key) ? (ConsoleKeyInfo?)key : null
            );
        }
    }
}
