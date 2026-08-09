using System.Text.Json;
using FluentAssertions;
using Spectre.Console.Testing;
using Tandem.Infrastructure.Dashboard;
using Tandem.Ledger;
using Tandem.Tool;

namespace Tandem.Tests.Infrastructure;

public sealed class DashboardTests : IDisposable
{
    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void TerminalDashboard_ExitsForEitherRedirectedStream(
        bool terminal,
        bool inputRedirected,
        bool outputRedirected,
        bool expected
    ) =>
        DashboardLoop
            .ShouldExitAfterTerminal(terminal, inputRedirected, outputRedirected)
            .Should()
            .Be(expected);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tandem-dashboard-{Guid.NewGuid():N}"
    );

    [Fact]
    public async Task DashboardProjectsCommittedSqliteFactsAndExternalPublicationOnRefresh()
    {
        var (store, runId) = await CreateAsync();
        var ledger = store.ForRun(runId);
        var observer = new SqlitePipelineObserver(ledger);
        await observer.ObserveAsync(
            new PipelineStepStarted(runId, "executor"),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineStepCompleted(
                runId,
                "executor",
                new PipelineRunOutcome(
                    StandardOutcomeKinds.Success,
                    "executor",
                    "complete",
                    default,
                    TimeSpan.FromSeconds(1)
                )
            ),
            CancellationToken.None
        );
        var candidate = new PublicationCandidateDocument(
            "candidate",
            "repo",
            "/workspace",
            "packet",
            "base",
            "sha"
        );
        var delivery = new DeliveryLedger(ledger);
        await delivery.AcceptPublicationCandidateAsync(
            "candidate",
            candidate,
            CancellationToken.None
        );
        await store.CompleteRunAsync(runId, LedgerRunStatus.Ready);

        var model = DashboardReducer.ApplyJournal(
            new DashboardModel(),
            await ledger.ReadAfterAsync(PipelineJournal.Stream, 0)
        );
        model = DashboardReducer.ApplyRun(model, await store.GetRunAsync(runId));
        model = DashboardReducer.ApplyDelivery(
            model,
            await delivery.ReadPublicationCandidateAsync(CancellationToken.None),
            await ledger.ReadAsync(DeliveryLedger.VerificationResults),
            await ledger.ReadAsync(DeliveryLedger.PublicationResults)
        );
        model.Status.Should().Be(RunStatus.Ready);
        model.CandidateSha.Should().Be("sha");
        model.PipelineHistory.Should().ContainSingle(item => item.StepId == "executor");

        await delivery.AcceptPublicationResultAsync(
            new PublicationResultRecord("repo", "tandem/change", "sha", false),
            CancellationToken.None
        );
        model = DashboardReducer.ApplyDelivery(
            model,
            await delivery.ReadPublicationCandidateAsync(CancellationToken.None),
            await ledger.ReadAsync(DeliveryLedger.VerificationResults),
            await ledger.ReadAsync(DeliveryLedger.PublicationResults)
        );
        model.PublishedBranch.Should().Be("tandem/change");
        File.Exists(Path.Combine(_directory, "events.jsonl")).Should().BeFalse();
    }

    [Fact]
    public async Task TextAndReasoningAreLiveBoundedAndAbsentAfterReconstruction()
    {
        var (_, runId) = await CreateAsync();
        var live = new LiveTranscript(2);
        await live.ObserveAsync(
            new PipelineAgentUpdated(runId, "executor", new AgentUpdate.Text("one")),
            CancellationToken.None
        );
        await live.ObserveAsync(
            new PipelineAgentUpdated(runId, "planner", new AgentUpdate.Reasoning("two")),
            CancellationToken.None
        );
        await live.ObserveAsync(
            new PipelineAgentUpdated(runId, "reviewer", new AgentUpdate.Text("three")),
            CancellationToken.None
        );
        await live.ObserveAsync(
            new PipelineAgentUpdated(
                runId,
                "reviewer",
                new AgentUpdate.ToolStarted(
                    "id",
                    "secret",
                    JsonSerializer.SerializeToElement(new { payload = "hidden" })
                )
            ),
            CancellationToken.None
        );

        live.Snapshot().Select(entry => entry.Line.Text).Should().Equal("two", "three");
        DashboardReducer
            .ApplyTranscript(new DashboardModel(), live.Snapshot())
            .Transcript.Should()
            .HaveCount(2);
        new DashboardModel().Transcript.Should().BeEmpty();

        var characterBounded = new LiveTranscript(10, 5);
        await characterBounded.ObserveAsync(
            new PipelineAgentUpdated(runId, "executor", new AgentUpdate.Text("1234")),
            CancellationToken.None
        );
        await characterBounded.ObserveAsync(
            new PipelineAgentUpdated(runId, "executor", new AgentUpdate.Text("5678")),
            CancellationToken.None
        );
        characterBounded.Snapshot().Should().ContainSingle().Which.Line.Text.Should().Be("45678");
    }

    [Fact]
    public void JournalProjection_RetainsRepeatedVisitsAndHumanHistory()
    {
        var runId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var question = JsonSerializer.SerializeToElement(
            new HumanQuestion("Proceed?", "Approval required"),
            JsonSerializerOptions.Web
        );
        var answer = JsonSerializer.SerializeToElement(
            new HumanAnswer("Proceed"),
            JsonSerializerOptions.Web
        );
        var entries = new[]
        {
            Entry(1, new RuntimeJournalRecord(RuntimeJournalKind.StepStarted, "executor"), now),
            Entry(
                2,
                new RuntimeJournalRecord(
                    RuntimeJournalKind.StepCompleted,
                    "executor",
                    Result: "first"
                ),
                now.AddSeconds(1)
            ),
            Entry(
                3,
                new RuntimeJournalRecord(RuntimeJournalKind.StepStarted, "executor"),
                now.AddSeconds(2)
            ),
            Entry(
                4,
                new RuntimeJournalRecord(
                    RuntimeJournalKind.StepCompleted,
                    "executor",
                    Result: "second"
                ),
                now.AddSeconds(3)
            ),
            Entry(
                5,
                new RuntimeJournalRecord(
                    RuntimeJournalKind.InteractionRequested,
                    "human-review",
                    "request-1",
                    nameof(HumanQuestion),
                    Payload: question
                ),
                now.AddSeconds(4)
            ),
            Entry(
                6,
                new RuntimeJournalRecord(
                    RuntimeJournalKind.InteractionAnswered,
                    "human-review",
                    "request-1",
                    nameof(HumanAnswer),
                    Payload: answer
                ),
                now.AddSeconds(5)
            ),
        };

        var model = DashboardReducer.ApplyJournal(new DashboardModel(), entries);

        model.PipelineHistory.Count(item => item.StepId == "executor").Should().Be(2);
        model.PipelineHistory.Count(item => item.IsHuman is true).Should().Be(2);
        model.PendingHumanRequest.Should().BeNull();
    }

    [Theory]
    [InlineData(LedgerRunStatus.Ready, RunStatus.Ready)]
    [InlineData(LedgerRunStatus.Failed, RunStatus.Failed)]
    [InlineData(LedgerRunStatus.Faulted, RunStatus.Faulted)]
    [InlineData(LedgerRunStatus.Cancelled, RunStatus.Cancelled)]
    public void RunProjection_MapsTerminalStatus(
        LedgerRunStatus ledgerStatus,
        RunStatus dashboardStatus
    )
    {
        var now = DateTimeOffset.UtcNow;
        var run = new LedgerRun(Guid.CreateVersion7(), "delivery", ledgerStatus, now, now, now);

        var model = DashboardReducer.ApplyRun(new DashboardModel(), run);

        model.Status.Should().Be(dashboardStatus);
        model.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Render_NarrowTerminal_DoesNotThrow()
    {
        var console = new TestConsole().Width(60).Height(24);
        var renderer = new DashboardRenderer(console);

        var act = () => renderer.Render(ReadyModel());

        act.Should().NotThrow();
        console.Output.Split('\n').Should().HaveCountLessThanOrEqualTo(25);
    }

    [Fact]
    public void Render_WideTerminal_DoesNotThrow()
    {
        var console = new TestConsole().Width(140).Height(40);
        var renderer = new DashboardRenderer(console);

        var act = () => renderer.Render(ReadyModel());

        act.Should().NotThrow();
        console.Output.Split('\n').Should().HaveCountLessThanOrEqualTo(41);
        console.Output.Should().Contain("complete");
        console.Output.Should().NotContain("Ready  waiting");
    }

    [Fact]
    public void Render_PendingHuman_DoesNotThrowAndShowsQuestion()
    {
        var console = new TestConsole().Width(100).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = new DashboardModel
        {
            Status = RunStatus.WaitingForHuman,
            PendingHumanRequest = new HumanRequestView("PlannerHumanInput", "Which?", "ambiguous"),
        };

        var act = () => renderer.Render(model);

        act.Should().NotThrow();
        console.Output.Should().Contain("Which?").And.Contain("ambiguous");
    }

    [Fact]
    public void Render_MergedTranscript_ShowsAllSteps()
    {
        var console = new TestConsole().Width(120).Height(40);
        var renderer = new DashboardRenderer(console);
        var model = Model(
            ("executor", "investigating repo"),
            ("planner", "decision: proceed"),
            ("executor", "implementing now")
        );

        renderer.Render(model);

        console.Output.Should().Contain("investigating repo");
        console.Output.Should().Contain("decision: proceed");
        console.Output.Should().Contain("implementing now");
    }

    [Fact]
    public void Render_CompleteJson_PrettyPrintsAfterStreamingCompletes()
    {
        var console = new TestConsole().Width(120).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = Model(
            ("planner", "{\"decision\":\"Proceed\",\"approved\":true,\"constraints\":[]}")
        );

        renderer.Render(model);

        console.Output.Should().Contain("\"decision\": \"Proceed\"");
        console.Output.Should().Contain("\"approved\": true");
        console
            .Output.Split('\n')
            .Count(line => line.Contains("decision") || line.Contains("approved"))
            .Should()
            .Be(2);
    }

    [Fact]
    public void Render_IncompleteStreamedJson_RemainsPlainText()
    {
        var console = new TestConsole().Width(120).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = Model(("planner", "{\"decision\":\"Proceed\",\"constraints\":"));

        renderer.Render(model);

        console.Output.Should().Contain("{\"decision\":\"Proceed\",\"constraints\":");
    }

    [Fact]
    public void Render_CompletedJsonFence_PrettyPrintsWithoutFence()
    {
        var console = new TestConsole().Width(120).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = Model(("reviewer", "```json\n{\"decision\":\"Accept\"}\n```"));

        renderer.Render(model);

        console.Output.Should().Contain("\"decision\": \"Accept\"");
        console.Output.Should().NotContain("```json");
    }

    [Fact]
    public void Render_PrefixedJson_PrettyPrintsAndPreservesCharacters()
    {
        var console = new TestConsole().Width(140).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = Model(
            (
                "executor",
                "planner request:\n{\"proposedApproach\":\"Call `markComplete` → return todo\"}"
            )
        );

        renderer.Render(model);

        console.Output.Should().Contain("planner request:");
        console
            .Output.Should()
            .Contain("\"proposedApproach\": \"Call `markComplete` → return todo\"");
        console.Output.Should().NotContain("\\u0060");
    }

    [Fact]
    public void Render_LongJsonValue_WrapsInsideTheContentColumn()
    {
        var console = new TestConsole().Width(80).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = Model(
            (
                "executor",
                "command:\n{\"result\":\"First segment followed by a deliberately long second segment that must wrap inside the JSON content column.\"}"
            )
        );

        renderer.Render(model);

        var continuation = console
            .Output.Split('\n')
            .Single(line => line.Contains("second segment"));
        continuation.IndexOf("second segment", StringComparison.Ordinal).Should().BeGreaterThan(10);
    }

    [Fact]
    public void Render_CoalescedCorrection_PrettyPrintsBothJsonDocuments()
    {
        var console = new TestConsole().Width(120).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = Model(
            (
                "planner",
                "{\"decision\":\"ProceedWithConstraints\",\"constraints\":[\"\"]} {\"decision\":\"Proceed\",\"constraints\":[]}"
            )
        );

        renderer.Render(model);

        console.Output.Should().Contain("\"decision\": \"ProceedWithConstraints\"");
        console.Output.Should().Contain("\"decision\": \"Proceed\"");
        console.Output.Split('\n').Count(line => line.Contains("\"decision\"")).Should().Be(2);
    }

    [Fact]
    public void Render_ScrollHome_ShowsOlderTranscriptAndFollowHint()
    {
        var console = new TestConsole().Width(120).Height(16);
        var renderer = new DashboardRenderer(console);
        var model = Model(
            Enumerable.Range(0, 40).Select(index => ("executor", $"line-{index:D2}\n")).ToArray()
        );

        renderer.Render(model);
        console.Output.Should().Contain("line-39");

        renderer.ScrollHome();
        renderer.Render(model);

        console.Output.Should().Contain("line-00");
        console.Output.Should().Contain("End follow");
    }

    [Fact]
    public void Render_MergedTranscript_TagsLinesWithStepId()
    {
        var console = new TestConsole().Width(120).Height(40);
        var renderer = new DashboardRenderer(console);
        var model = Model(("executor", "checking files"), ("planner", "approving approach"));

        renderer.Render(model);

        console.Output.Should().Contain("[executor]");
        console.Output.Should().Contain("[ planner]");
    }

    [Fact]
    public void Render_StepTags_CenterShortNamesToOneGutterWidth()
    {
        var console = new TestConsole().Width(120).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = Model(("executor", "implementation"), ("verify", "verification"));

        renderer.Render(model);

        console.Output.Should().Contain("[executor]");
        console.Output.Should().Contain("[ verify ]");
        var implementation = console
            .Output.Split('\n')
            .Single(line => line.Contains("implementation"));
        var verification = console.Output.Split('\n').Single(line => line.Contains("verification"));
        implementation
            .IndexOf("implementation", StringComparison.Ordinal)
            .Should()
            .Be(verification.IndexOf("verification", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_MergedTranscript_PreservesEntryOrder()
    {
        var console = new TestConsole().Width(120).Height(40);
        var renderer = new DashboardRenderer(console);
        var model = Model(
            ("executor", "first turn"),
            ("planner", "planner response"),
            ("executor", "second turn")
        );

        renderer.Render(model);

        var output = console.Output;
        output
            .IndexOf("first turn", StringComparison.Ordinal)
            .Should()
            .BeLessThan(output.IndexOf("planner response", StringComparison.Ordinal));
        output
            .IndexOf("planner response", StringComparison.Ordinal)
            .Should()
            .BeLessThan(output.IndexOf("second turn", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_MultilineMessage_LabelsOnceAndPreservesIndentation()
    {
        var console = new TestConsole().Width(140).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = Model(("executor", "command:\n{\n  \"verification\": \"passed\"\n}"));

        renderer.Render(model);

        var output = console.Output;
        output.Split("[executor]", StringSplitOptions.None).Should().HaveCount(2);
        var braceLine = output.Split('\n').Single(line => line.Contains('{'));
        var verificationLine = output.Split('\n').Single(line => line.Contains("\"verification\""));
        verificationLine.IndexOf('"').Should().Be(braceLine.IndexOf('{') + 2);
    }

    [Fact]
    public void Render_LongOutput_KeepsNewestTranscriptVisible()
    {
        var console = new TestConsole().Width(100).Height(18);
        var renderer = new DashboardRenderer(console);
        var longOutput =
            "verification:\n{\n"
            + string.Join(
                ",\n",
                Enumerable.Range(1, 30).Select(i => $"  \"line{i}\": \"value{i}\"")
            )
            + "\n}";
        var model = Model(("verify", longOutput), ("planner", "LATEST PLANNER OUTPUT"));

        renderer.Render(model);

        console.Output.Should().Contain("LATEST PLANNER OUTPUT");
        console.Output.Split('\n').Should().HaveCountLessThanOrEqualTo(19);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private async Task<(SqliteLedgerStore Store, Guid RunId)> CreateAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteLedgerStore(Path.Combine(_directory, "ledger.sqlite3"));
        await store.InitializeAsync();
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        return (store, runId);
    }

    private static AcceptedLedgerEntry<RuntimeJournalRecord> Entry(
        long sequence,
        RuntimeJournalRecord record,
        DateTimeOffset recordedAt
    ) => new(sequence, $"entry-{sequence}", record, recordedAt);

    private static DashboardModel ReadyModel() =>
        Model(("executor", "hello")) with
        {
            RunId = "abc",
            Status = RunStatus.Ready,
            CandidateSha = "abc123",
            CompletedAt = DateTimeOffset.UtcNow,
            ActiveStepId = null,
        };

    private static DashboardModel Model(params (string StepId, string Text)[] entries)
    {
        var now = DateTimeOffset.UtcNow;
        var transcript = entries
            .Select(
                (entry, index) =>
                    new TranscriptEntry(
                        entry.StepId,
                        new TranscriptLine("text", entry.Text, now.AddMilliseconds(index))
                    )
            )
            .ToArray();
        var stepIds = entries.Select(entry => entry.StepId).Distinct().ToArray();
        var activeStepId = entries.LastOrDefault().StepId;
        var steps = stepIds
            .Select(stepId => new StepTranscript(
                stepId,
                stepId == activeStepId,
                false,
                now,
                null,
                null,
                null,
                null,
                transcript
                    .Where(entry => entry.StepId == stepId)
                    .Select(entry => entry.Line)
                    .ToArray()
            ))
            .ToArray();
        return new DashboardModel
        {
            RunId = "abc",
            ActiveStepId = activeStepId,
            StartedAt = now,
            Steps = steps,
            Transcript = transcript,
        };
    }
}
