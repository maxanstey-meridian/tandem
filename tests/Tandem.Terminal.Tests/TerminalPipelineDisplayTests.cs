using System.Text.Json;
using FluentAssertions;
using Spectre.Console.Testing;

namespace Tandem.Terminal.Tests;

public sealed class TerminalPipelineDisplayTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ModeRequiresBothTerminalStreams(bool input, bool output, bool expected)
    {
        var display = Create(new TestConsole(), new(input, output));

        display.IsInteractive.Should().Be(expected);
    }

    [Fact]
    public async Task ObserverRetainsRepeatedStepVisitsAndDurations()
    {
        var model = Model();
        var observer = new ModelObserver(model);
        var runId = _runId;

        await observer.ObserveAsync(new PipelineStepStarted(runId, "work"), default);
        await observer.ObserveAsync(
            new PipelineStepCompleted(runId, "work", Outcome("first", 1)),
            default
        );
        await observer.ObserveAsync(new PipelineStepStarted(runId, "work"), default);
        await observer.ObserveAsync(
            new PipelineStepCompleted(runId, "work", Outcome("second", 2)),
            default
        );

        model.Snapshot().Visits.Should().HaveCount(2);
        model.Snapshot().Visits.Select(visit => visit.Summary).Should().Equal("first", "second");
        model
            .Snapshot()
            .Visits.Select(visit => visit.Duration)
            .Should()
            .Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TranscriptCoalescesAdjacentKindsAndBoundsCharactersAndEntries()
    {
        var model = Model(entries: 2, characters: 5);
        var observer = new ModelObserver(model);
        var runId = _runId;

        await observer.ObserveAsync(
            new PipelineAgentUpdated(runId, "one", new AgentUpdate.Text("12")),
            default
        );
        await observer.ObserveAsync(
            new PipelineAgentUpdated(runId, "one", new AgentUpdate.Text("34")),
            default
        );
        await observer.ObserveAsync(
            new PipelineAgentUpdated(runId, "one", new AgentUpdate.Reasoning("5")),
            default
        );
        await observer.ObserveAsync(
            new PipelineAgentUpdated(runId, "two", new AgentUpdate.Text("67")),
            default
        );

        var transcript = model.Snapshot().Transcript;
        transcript.Should().HaveCount(2);
        transcript.Select(entry => entry.Text).Should().Equal("5", "67");
    }

    [Fact]
    public async Task UsageAndInteractionWaitingAreProjectedWithoutReadingPayloads()
    {
        var model = Model();
        var observer = new ModelObserver(model);
        var runId = _runId;
        var request = new PipelineInteractionRequested<string>(
            runId,
            "approval",
            "request",
            "secret"
        );

        model.Apply(new PipelineStepStarted(runId, "agent"));
        await observer.ObserveAsync(
            new PipelineAgentUsage(runId, "agent", 10, 4, 30, 200_000),
            default
        );
        await observer.ObserveAsync(request, default);
        model.Snapshot().Status.Should().Be(TerminalPipelineStatus.WaitingForInteraction);
        await observer.ObserveAsync(
            new PipelineInteractionAnswered<string>(runId, "approval", "request", "answer"),
            default
        );

        var snapshot = model.Snapshot();
        snapshot.InputTokens.Should().Be(10);
        snapshot.OutputTokens.Should().Be(4);
        snapshot.CurrentContextTokens.Should().Be(30);
        snapshot.ContextWindowTokens.Should().Be(200_000);
        snapshot.WaitingInteractions.Should().Be(0);
        snapshot.Status.Should().Be(TerminalPipelineStatus.Running);
    }

    [Fact]
    public async Task ObserverSupervisesAnotherRunsObservationAsPresentationFailure()
    {
        await using var display = Create(new TestConsole(), new(false, false));

        await display.Observer.ObserveAsync(
            new PipelineStepStarted(Guid.CreateVersion7(), "foreign"),
            default
        );
        await display.StartAsync();
        var act = () => display.WaitForCleanupAsync();

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Terminal presentation failed.");
    }

    [Fact]
    public async Task RedirectedOutputIsAnsiFreeAndChronological()
    {
        var console = new TestConsole();
        await using var display = Create(console, new(false, false));
        var runId = _runId;
        await display.StartAsync();

        await display.Observer.ObserveAsync(
            new PipelineAgentUpdated(runId, "agent", new AgentUpdate.Text("first")),
            default
        );
        await display.Observer.ObserveAsync(new PipelineStepStarted(runId, "verify"), default);
        await display.Observer.ObserveAsync(
            new PipelineStepCompleted(runId, "verify", Outcome("second", 1)),
            default
        );
        await display.SucceededAsync("third");
        await display.WaitForCleanupAsync();

        console.Output.Should().NotContain("\u001b[");
        console
            .Output.IndexOf("first", StringComparison.Ordinal)
            .Should()
            .BeLessThan(console.Output.IndexOf("second", StringComparison.Ordinal));
        console
            .Output.IndexOf("second", StringComparison.Ordinal)
            .Should()
            .BeLessThan(console.Output.IndexOf("third", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeActivityIsVisibleAndControlCharactersAreRemoved()
    {
        var console = new TestConsole().Width(240);
        var workingDirectory = Path.Combine(Path.GetTempPath(), "terminal-launch");
        var invocationDirectory = Path.Combine(Path.GetTempPath(), "agent-workspace");
        await using var display = new TerminalPipelineDisplay(
            Inspection(),
            _runId,
            new TerminalDisplayOptions
            {
                Console = console,
                Capabilities = new(false, false),
                WorkingDirectory = workingDirectory,
                RefreshInterval = TimeSpan.FromMilliseconds(1),
            }
        );
        await display.StartAsync();

        using var arguments = JsonDocument.Parse("{\"path\":\"src/file.cs\",\"staged\":false}");
        await display.Observer.ObserveAsync(
            new PipelineAgentUpdated(
                _runId,
                "agent",
                new AgentUpdate.ToolStarted("call", "search", arguments.RootElement)
                {
                    WorkingDirectory = invocationDirectory,
                }
            ),
            default
        );
        await display.Observer.ObserveAsync(
            new PipelineCommandOutput(_runId, "verify", "test", "\u001b[31mpassed", 0),
            default
        );
        await display.Observer.ObserveAsync(
            new PipelineActionCompleted(
                _runId,
                "agent",
                "invocation",
                "publish",
                "Write",
                "Completed"
            ),
            default
        );
        await display.SucceededAsync("complete");
        await display.WaitForCleanupAsync();

        console
            .Output.Should()
            .Contain(
                $"tool search path=\"src/file.cs\" staged=false in {invocationDirectory} started"
            );
        console.Output.Should().Contain("command test exited 0: [31mpassed");
        console.Output.Should().Contain("action publish Completed");
        console.Output.Should().NotContain("\u001b");
    }

    [Fact]
    public async Task PlainOutputTruncatesArgumentsOnlyForConfiguredTools()
    {
        var console = new TestConsole().Width(240);
        var invocationDirectory = Path.Combine(Path.GetTempPath(), "agent-workspace");
        await using var display = new TerminalPipelineDisplay(
            Inspection(),
            _runId,
            new TerminalDisplayOptions
            {
                Console = console,
                Capabilities = new(false, false),
                TruncatedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "write_checkpoint",
                },
            }
        );
        await display.StartAsync();

        using var arguments = JsonDocument.Parse("{\"summary\":\"Detailed checkpoint\"}");
        await display.Observer.ObserveAsync(
            new PipelineAgentUpdated(
                _runId,
                "executor",
                new AgentUpdate.ToolStarted(
                    "checkpoint-call",
                    "write_checkpoint",
                    arguments.RootElement
                )
                {
                    WorkingDirectory = invocationDirectory,
                }
            ),
            default
        );
        await display.Observer.ObserveAsync(
            new PipelineAgentUpdated(
                _runId,
                "executor",
                new AgentUpdate.ToolStarted("case-call", "WRITE_CHECKPOINT", arguments.RootElement)
                {
                    WorkingDirectory = invocationDirectory,
                }
            ),
            default
        );
        await display.Observer.ObserveAsync(
            new PipelineAgentUpdated(
                _runId,
                "executor",
                new AgentUpdate.ToolStarted("search-call", "search", arguments.RootElement)
                {
                    WorkingDirectory = invocationDirectory,
                }
            ),
            default
        );
        await display.SucceededAsync("complete");
        await display.WaitForCleanupAsync();

        console
            .Output.Should()
            .Contain($"tool write_checkpoint in {invocationDirectory} started")
            .And.Contain(
                $"tool WRITE_CHECKPOINT summary=\"Detailed checkpoint\" in {invocationDirectory} started"
            )
            .And.Contain(
                $"tool search summary=\"Detailed checkpoint\" in {invocationDirectory} started"
            );
    }

    [Fact]
    public void AcceptedCapabilitySummaryAppearsInInteractiveTranscript()
    {
        var model = new TerminalModel(
            "pipeline",
            _runId,
            TimeProvider.System,
            100,
            10_000,
            null,
            null
        );

        model.Apply(
            new PipelineCapabilityAccepted(
                _runId,
                "implementer",
                "invocation",
                "capability",
                "submit_implementation",
                "accepted-call",
                "Implementation:\nconst value = true;"
            )
        );

        model
            .Snapshot()
            .Transcript.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new TranscriptEntry(
                    "implementer",
                    TranscriptKind.Text,
                    "Implementation:\nconst value = true;"
                )
            );
    }

    [Fact]
    public void EmptyAcceptedCapabilitySummaryLeavesOnlySemanticPayload()
    {
        var model = new TerminalModel(
            "pipeline",
            _runId,
            TimeProvider.System,
            100,
            10_000,
            null,
            null
        );
        using var payload = JsonDocument.Parse("{\"summary\":\"Detailed checkpoint\"}");

        model.Apply(
            new PipelineCapabilityAccepted(
                _runId,
                "executor",
                "invocation",
                "capability",
                "write_checkpoint",
                "accepted-call",
                "",
                "checkpoint",
                payload.RootElement
            )
        );

        model
            .Snapshot()
            .Transcript.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new TranscriptEntry(
                    "executor",
                    TranscriptKind.Semantic,
                    "{\"summary\":\"Detailed checkpoint\"}"
                )
            );
    }

    [Fact]
    public void ModelNameFollowsTheActiveParticipantRuntimeSelection()
    {
        var model = new TerminalModel(
            "pipeline",
            _runId,
            TimeProvider.System,
            100,
            10_000,
            null,
            null
        );

        model.Apply(new PipelineStepStarted(_runId, "implementer"));
        model.Apply(
            new PipelineAgentUpdated(
                _runId,
                "implementer",
                new AgentUpdate.ModelSelected("deepseek/deepseek-v4-flash-0731")
            )
        );
        model.Snapshot().ModelName.Should().Be("deepseek/deepseek-v4-flash-0731");
        model.Apply(new PipelineStepCompleted(_runId, "implementer", Outcome("implemented", 1)));
        model.Apply(new PipelineStepStarted(_runId, "reviewer"));
        model.Snapshot().ModelName.Should().BeNull();
        model.Apply(
            new PipelineAgentUpdated(
                _runId,
                "reviewer",
                new AgentUpdate.ModelSelected("gpt-5.6-sol")
            )
        );
        model.Snapshot().ModelName.Should().Be("gpt-5.6-sol");
        model.Apply(new PipelineStepCompleted(_runId, "reviewer", Outcome("accepted", 1)));
        model.Apply(new PipelineStepStarted(_runId, "done"));
        model.Finish(TerminalPipelineStatus.Succeeded, "complete");

        model.Snapshot().ModelName.Should().BeNull();
    }

    [Fact]
    public void ContextUsageFollowsTheActiveParticipant()
    {
        var model = new TerminalModel(
            "pipeline",
            _runId,
            TimeProvider.System,
            100,
            10_000,
            null,
            null
        );

        model.Apply(new PipelineStepStarted(_runId, "executor"));
        model.Apply(new PipelineAgentUsage(_runId, "executor", 10, 2, 12_000, 200_000));
        model.Snapshot().CurrentContextTokens.Should().Be(12_000);
        model.Snapshot().ContextWindowTokens.Should().Be(200_000);

        model.Apply(new PipelineStepStarted(_runId, "reviewer"));
        model.Snapshot().CurrentContextTokens.Should().Be(0);
        model.Snapshot().ContextWindowTokens.Should().BeNull();
        model.Apply(new PipelineAgentUsage(_runId, "reviewer", 5, 1, 6_000, 100_000));
        model.Snapshot().CurrentContextTokens.Should().Be(6_000);
        model.Snapshot().ContextWindowTokens.Should().Be(100_000);

        model.Apply(new PipelineStepStarted(_runId, "executor"));
        model.Snapshot().CurrentContextTokens.Should().Be(12_000);
        model.Snapshot().ContextWindowTokens.Should().Be(200_000);
    }

    [Fact]
    public void ParallelContextUsageFollowsTheMostRecentActiveParticipant()
    {
        var model = new TerminalModel(
            "pipeline",
            _runId,
            TimeProvider.System,
            100,
            10_000,
            null,
            null
        );

        model.Apply(new PipelineStepStarted(_runId, "first"));
        model.Apply(new PipelineStepStarted(_runId, "second"));
        model.Apply(new PipelineAgentUsage(_runId, "first", 10, 2, 12_000, 200_000));
        model.Snapshot().CurrentContextTokens.Should().Be(12_000);

        model.Apply(new PipelineAgentUsage(_runId, "second", 5, 1, 6_000, 100_000));
        model.Snapshot().CurrentContextTokens.Should().Be(6_000);
        model.Snapshot().ContextWindowTokens.Should().Be(100_000);

        model.Apply(new PipelineStepCompleted(_runId, "second", Outcome("complete", 1)));
        model.Snapshot().CurrentContextTokens.Should().Be(12_000);
        model.Snapshot().ContextWindowTokens.Should().Be(200_000);

        model.Apply(new PipelineStepCompleted(_runId, "first", Outcome("complete", 1)));
        model.Snapshot().CurrentContextTokens.Should().Be(0);
        model.Snapshot().ContextWindowTokens.Should().BeNull();
    }

    [Fact]
    public void NarrowRendererDoesNotThrow()
    {
        var console = new TestConsole().Width(30).Height(10);
        var renderer = new TerminalRenderer(console);

        var act = () => renderer.Render(Model().Snapshot());

        act.Should().NotThrow();
    }

    [Fact]
    public async Task CancellationCallbackRunsExactlyOnceAndCompletionRestoresAlternateScreen()
    {
        var console = new TestConsole().Width(80).Height(20);
        TerminalPipelineDisplay? display = null;
        var calls = 0;
        display = Create(
            console,
            new(true, true),
            new QueueInput(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)),
            async _ =>
            {
                Interlocked.Increment(ref calls);
                await display!.CancelledAsync("cancelled");
            }
        );
        await using (display)
        {
            await display.StartAsync();
            await display.WaitForCleanupAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }

        calls.Should().Be(1);
    }

    [Fact]
    public async Task QuitIsNotStarvedByQueuedScrollInput()
    {
        var console = new TestConsole().Width(80).Height(20);
        var keys = Enumerable
            .Repeat(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false), 500)
            .Append(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false))
            .ToArray();
        var calls = 0;
        TerminalPipelineDisplay? display = null;
        display = Create(
            console,
            new(true, true),
            new QueueInput(keys),
            async _ =>
            {
                calls++;
                await display!.CancelledAsync("cancelled");
            }
        );
        await using (display)
        {
            await display.StartAsync();
            await display.WaitForCleanupAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }

        calls.Should().Be(1);
    }

    [Fact]
    public async Task InteractiveDisplayRemainsOpenAfterTerminalCompletionUntilQuit()
    {
        var console = new TestConsole().Width(80).Height(20);
        var input = new QueueInput();
        await using var display = Create(console, new(true, true), input);
        await display.StartAsync();

        await display.SucceededAsync("complete");
        var cleanup = display.WaitForCleanupAsync();
        await Task.Delay(50);

        cleanup.IsCompleted.Should().BeFalse();
        input.Enqueue(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false));

        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(TerminalPipelineStatus.Faulted)]
    [InlineData(TerminalPipelineStatus.Cancelled)]
    public async Task InteractiveFaultOrCancellationRestoresTerminalWithoutAnotherKey(
        TerminalPipelineStatus status
    )
    {
        var console = new TestConsole().Width(80).Height(20);
        await using var display = Create(console, new(true, true), new QueueInput());
        await display.StartAsync();

        if (status == TerminalPipelineStatus.Faulted)
        {
            await display.FaultedAsync("faulted");
        }
        else
        {
            await display.CancelledAsync("cancelled");
        }

        await display.WaitForCleanupAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task QuitAfterTerminalCompletionClosesDisplayWithoutCancellingRun()
    {
        var cancellations = 0;
        await using var display = new TerminalPipelineDisplay(
            Inspection(),
            _runId,
            new TerminalDisplayOptions
            {
                Console = new TestConsole().Width(80).Height(20),
                Capabilities = new(true, true),
                KeyInput = new QueueInput(
                    new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)
                ),
                CancelAsync = _ =>
                {
                    cancellations++;
                    return ValueTask.CompletedTask;
                },
                RefreshInterval = TimeSpan.FromMilliseconds(1),
            }
        );
        await display.SucceededAsync("complete");

        await display.StartAsync();
        await display.WaitForCleanupAsync().WaitAsync(TimeSpan.FromSeconds(2));

        cancellations.Should().Be(0);
    }

    [Fact]
    public void TerminalTextNormalizesCursorControls()
    {
        TerminalText.Sanitize("first\rsecond\tvalue\u001b").Should().Be("first\nsecond  value");
    }

    [Fact]
    public async Task InteractivePresentationFailureCancelsRunAndSurfacesAfterCleanup()
    {
        var console = new TestConsole().Width(80).Height(20);
        var cancellations = 0;
        await using var display = new TerminalPipelineDisplay(
            Inspection(),
            _runId,
            new TerminalDisplayOptions
            {
                Console = console,
                Capabilities = new(true, true),
                KeyInput = new QueueInput(),
                ReadPipelineEntriesAsync = _ =>
                    throw new InvalidOperationException("details failed"),
                CancelAsync = _ =>
                {
                    Interlocked.Increment(ref cancellations);
                    return ValueTask.CompletedTask;
                },
                RefreshInterval = TimeSpan.FromMilliseconds(1),
            }
        );
        await display.StartAsync();

        var act = () => display.WaitForCleanupAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Terminal presentation failed.");
        cancellations.Should().Be(1);
    }

    [Fact]
    public async Task OnlyOneInteractivePresentationOwnsTheProcessTerminal()
    {
        var first = Create(
            new TestConsole().Width(80).Height(20),
            new(true, true),
            new QueueInput()
        );
        await using var second = Create(
            new TestConsole().Width(80).Height(20),
            new(true, true),
            new QueueInput()
        );
        await first.StartAsync();

        var act = () => second.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already active*");
        await first.DisposeAsync();
    }

    [Fact]
    public async Task HostFormattedInteractionAcceptsTextAndAvailableKeyAction()
    {
        var console = new TestConsole().Width(100).Height(24);
        var submitted = "";
        var actionCalls = 0;
        await using var display = new TerminalPipelineDisplay(
            Inspection(),
            _runId,
            new TerminalDisplayOptions
            {
                Console = console,
                Capabilities = new(true, true),
                KeyInput = new QueueInput(
                    new ConsoleKeyInfo('o', ConsoleKey.O, false, false, false),
                    new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false),
                    new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
                    new ConsoleKeyInfo('p', ConsoleKey.P, false, false, false),
                    new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true)
                ),
                FormatInteraction = _ => new("Question?", "Reason"),
                SubmitTextAsync = (text, _) =>
                {
                    submitted = text;
                    return ValueTask.CompletedTask;
                },
                KeyActions =
                [
                    new(
                        ConsoleKey.P,
                        "publish",
                        _ =>
                        {
                            actionCalls++;
                            return ValueTask.CompletedTask;
                        }
                    ),
                ],
                RefreshInterval = TimeSpan.FromMilliseconds(1),
            }
        );
        await display.Observer.ObserveAsync(
            new PipelineInteractionRequested<string>(_runId, "human", "request", "payload"),
            default
        );

        await display.StartAsync();
        await display.WaitForCleanupAsync().WaitAsync(TimeSpan.FromSeconds(2));

        submitted.Should().Be("ok");
        actionCalls.Should().Be(1);
        console.Output.Should().Contain("Question?").And.Contain("Reason");
    }

    [Fact]
    public async Task InteractionAnswerContainingQIsSubmittedInsteadOfCancellingRun()
    {
        var submitted = "";
        var cancellations = 0;
        TerminalPipelineDisplay? display = null;
        display = new TerminalPipelineDisplay(
            Inspection(),
            _runId,
            new TerminalDisplayOptions
            {
                Console = new TestConsole().Width(100).Height(24),
                Capabilities = new(true, true),
                KeyInput = new QueueInput(
                    new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false),
                    new ConsoleKeyInfo('u', ConsoleKey.U, false, false, false),
                    new ConsoleKeyInfo('e', ConsoleKey.E, false, false, false),
                    new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false),
                    new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false),
                    new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false),
                    new ConsoleKeyInfo('o', ConsoleKey.O, false, false, false),
                    new ConsoleKeyInfo('n', ConsoleKey.N, false, false, false),
                    new ConsoleKeyInfo('?', ConsoleKey.Oem2, false, false, false),
                    new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)
                ),
                FormatInteraction = _ => new("Question?", "Reason"),
                SubmitTextAsync = async (text, _) =>
                {
                    submitted = text;
                    await display!.CancelledAsync("done");
                },
                CancelAsync = _ =>
                {
                    cancellations++;
                    return ValueTask.CompletedTask;
                },
                RefreshInterval = TimeSpan.FromMilliseconds(1),
            }
        );
        await using (display)
        {
            await display.Observer.ObserveAsync(
                new PipelineInteractionRequested<string>(_runId, "human", "request", "payload"),
                CancellationToken.None
            );

            await display.StartAsync(CancellationToken.None);
            await display
                .WaitForCleanupAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }

        submitted.Should().Be("question?");
        cancellations.Should().Be(0);
    }

    private static readonly Guid _runId = Guid.CreateVersion7();

    private static TerminalPipelineDisplay Create(
        TestConsole console,
        TerminalCapabilities capabilities,
        ITerminalKeyInput? input = null,
        Func<CancellationToken, ValueTask>? cancel = null
    ) =>
        new(
            Inspection(),
            _runId,
            new TerminalDisplayOptions
            {
                Console = console,
                Capabilities = capabilities,
                KeyInput = input,
                CancelAsync = cancel,
                RefreshInterval = TimeSpan.FromMilliseconds(1),
            }
        );

    private static TerminalModel Model(int entries = 10, int characters = 100) =>
        new("pipeline", _runId, TimeProvider.System, entries, characters, null, null);

    private static PipelineInspection Inspection() =>
        new("pipeline", null, "start", ["start"], [], [], ["start"], [], "", "");

    private static PipelineRunOutcome Outcome(string summary, int seconds) =>
        new(StandardOutcomeKinds.Success, "work", summary, default, TimeSpan.FromSeconds(seconds));

    private sealed class ModelObserver(TerminalModel model) : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            model.Apply(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueueInput(params ConsoleKeyInfo[] keys) : ITerminalKeyInput
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public void Enqueue(ConsoleKeyInfo key) => _keys.Enqueue(key);

        public ValueTask<ConsoleKeyInfo?> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_keys.TryDequeue(out var key) ? (ConsoleKeyInfo?)key : null);
    }
}
