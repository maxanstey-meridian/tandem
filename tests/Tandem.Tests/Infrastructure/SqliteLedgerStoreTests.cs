using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tandem.Ledger;
using Tandem.Tool;

namespace Tandem.Tests.Infrastructure;

public sealed class SqliteLedgerStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tandem-ledger-{Guid.NewGuid():N}"
    );

    [Fact]
    public async Task Initialize_CreatesMissingDatabaseParentDirectory()
    {
        var path = Path.Combine(_directory, "nested", "ledger.sqlite3");
        var store = new SqliteLedgerStore(path);

        await store.InitializeAsync();

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Entries_AreOrderedIdempotentAndDurableAcrossStoreInstances()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var stream = new LedgerStream<ProbeEntry>("probes", "test.probe");
        var store = await CreateStoreAsync(path);
        await store.CreateRunAsync(runId, "test");
        var ledger = store.ForRun(runId);

        var first = await ledger.AppendAsync(stream, "entry-1", new ProbeEntry("first", 1));
        var second = await ledger.AppendAsync(stream, "entry-2", new ProbeEntry("second", 2));
        var replay = await ledger.AppendAsync(stream, "entry-1", new ProbeEntry("first", 1));

        replay.Should().Be(first);
        second.Sequence.Should().Be(2);
        var reopened = await CreateStoreAsync(path);
        var entries = await reopened.ForRun(runId).ReadAsync(stream);
        entries.Select(entry => entry.Value.Name).Should().Equal("first", "second");
        entries.Select(entry => entry.Sequence).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Entries_CanBeReadIncrementallyAfterSequence()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var stream = new LedgerStream<ProbeEntry>("incremental", "test.probe");
        await store.CreateRunAsync(runId, "test");
        var ledger = store.ForRun(runId);
        await ledger.AppendAsync(stream, "entry-1", new ProbeEntry("first", 1));
        await ledger.AppendAsync(stream, "entry-2", new ProbeEntry("second", 2));
        await ledger.AppendAsync(stream, "entry-3", new ProbeEntry("third", 3));

        var entries = await ledger.ReadAfterAsync(stream, 1);

        entries.Select(entry => entry.Sequence).Should().Equal(2, 3);
        entries.Select(entry => entry.Value.Name).Should().Equal("second", "third");
    }

    [Fact]
    public async Task EntryIdentity_IsUniqueAcrossAWholeRun()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "test");
        var ledger = store.ForRun(runId);
        await ledger.AppendAsync(
            new LedgerStream<ProbeEntry>("first", "test.probe"),
            "same-id",
            new ProbeEntry("value", 1)
        );

        var differentStream = async () =>
            await ledger.AppendAsync(
                new LedgerStream<ProbeEntry>("second", "test.probe"),
                "same-id",
                new ProbeEntry("value", 1)
            );
        var differentContent = async () =>
            await ledger.AppendAsync(
                new LedgerStream<ProbeEntry>("first", "test.probe"),
                "same-id",
                new ProbeEntry("changed", 2)
            );

        await differentStream.Should().ThrowAsync<LedgerConflictException>();
        await differentContent.Should().ThrowAsync<LedgerConflictException>();
        var afterConflict = await ledger.AppendAsync(
            new LedgerStream<ProbeEntry>("first", "test.probe"),
            "next-id",
            new ProbeEntry("next", 3)
        );
        afterConflict.Sequence.Should().Be(2, "the conflicting transaction must roll back");
    }

    [Fact]
    public async Task Documents_UseCompareAndSwapWithIdempotentReplay()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "test");
        var ledger = store.ForRun(runId);
        var document = new LedgerDocument<ProbeEntry>("current-probe", "test.probe");

        var created = await ledger.WriteDocumentAsync(
            document,
            new ProbeEntry("first", 1),
            expectedVersion: 0
        );
        var replay = await ledger.WriteDocumentAsync(
            document,
            new ProbeEntry("first", 1),
            expectedVersion: 0
        );
        var updated = await ledger.WriteDocumentAsync(
            document,
            new ProbeEntry("second", 2),
            expectedVersion: 1
        );

        replay.Should().Be(created);
        updated.Version.Should().Be(2);
        (await ledger.ReadDocumentAsync(document)).Should().Be(updated);
        var stale = async () =>
            await ledger.WriteDocumentAsync(
                document,
                new ProbeEntry("stale", 3),
                expectedVersion: 1
            );
        await stale.Should().ThrowAsync<LedgerConflictException>();
    }

    [Fact]
    public async Task ConcurrentAppends_AreSerializedBySQLiteWithContiguousSequences()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var stream = new LedgerStream<ProbeEntry>("concurrent", "test.probe");
        var setup = await CreateStoreAsync(path);
        await setup.CreateRunAsync(runId, "test");

        await Task.WhenAll(
            Enumerable
                .Range(0, 24)
                .Select(async index =>
                {
                    var store = await CreateStoreAsync(path);
                    await store
                        .ForRun(runId)
                        .AppendAsync(
                            stream,
                            $"entry-{index}",
                            new ProbeEntry($"value-{index}", index)
                        );
                })
        );

        var entries = await setup.ForRun(runId).ReadAsync(stream);
        entries.Should().HaveCount(24);
        entries
            .Select(entry => entry.Sequence)
            .Should()
            .Equal(Enumerable.Range(1, 24).Select(i => (long)i));
    }

    [Fact]
    public async Task SeparateProcesses_AllocateContiguousSequencesAndConvergeSameIdentity()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();

        var distinct = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(index => RunWorkerAsync(path, runId, $"entry-{index}", $"value-{index}"))
        );
        var same = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ => RunWorkerAsync(path, runId, "same-entry", "same-value"))
        );

        distinct.Should().OnlyContain(result => result.ExitCode == 0);
        same.Should().OnlyContain(result => result.ExitCode == 0);
        same.Select(result => result.Output).Distinct().Should().ContainSingle();
        var store = await CreateStoreAsync(path);
        var entries = await store
            .ForRun(runId)
            .ReadAsync(new LedgerStream<ProcessEntry>("process-entries", "test.process-entry"));
        entries.Should().HaveCount(9);
        entries
            .Select(entry => entry.Sequence)
            .Should()
            .Equal(Enumerable.Range(1, 9).Select(i => (long)i));
    }

    [Fact]
    public async Task StorageNames_RejectAnotherContractKindNameOrVersion()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "test");
        var ledger = store.ForRun(runId);
        await ledger.AppendAsync(
            new LedgerStream<ProbeEntry>("claimed-name", "test.probe", 1),
            "entry-1",
            new ProbeEntry("value", 1)
        );

        var changedContract = async () =>
            await ledger.ReadAsync(
                new LedgerStream<ProbeEntry>("claimed-name", "test.other-probe", 1)
            );
        var changedVersion = async () =>
            await ledger.ReadAsync(new LedgerStream<ProbeEntry>("claimed-name", "test.probe", 2));
        var changedKind = async () =>
            await ledger.ReadDocumentAsync(
                new LedgerDocument<ProbeEntry>("claimed-name", "test.probe", 1)
            );

        await changedContract.Should().ThrowAsync<LedgerConflictException>();
        await changedVersion.Should().ThrowAsync<LedgerConflictException>();
        await changedKind.Should().ThrowAsync<LedgerConflictException>();
    }

    [Fact]
    public async Task RunsRemainIsolatedForTheSameStreamContract()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var firstRun = Guid.CreateVersion7();
        var secondRun = Guid.CreateVersion7();
        var stream = new LedgerStream<ProbeEntry>("isolated", "test.probe");
        await store.CreateRunAsync(firstRun, "test");
        await store.CreateRunAsync(secondRun, "test");
        await store.ForRun(firstRun).AppendAsync(stream, "entry", new ProbeEntry("first", 1));
        await store.ForRun(secondRun).AppendAsync(stream, "entry", new ProbeEntry("second", 2));

        (await store.ForRun(firstRun).ReadAsync(stream)).Single().Value.Name.Should().Be("first");
        (await store.ForRun(secondRun).ReadAsync(stream)).Single().Value.Name.Should().Be("second");
    }

    [Fact]
    public async Task LockContentionRetriesBoundedlyThenFails()
    {
        var path = DatabasePath();
        var setup = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        await setup.CreateRunAsync(runId, "test");
        var options = new SqliteLedgerOptions(
            TimeSpan.FromMilliseconds(20),
            1,
            TimeSpan.FromMilliseconds(10)
        );
        var store = new SqliteLedgerStore(path, options: options);
        await using var blocker = new SqliteConnection($"Data Source={path};Pooling=False");
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(deferred: false);
        var stopwatch = Stopwatch.StartNew();

        var append = async () =>
            await store
                .ForRun(runId)
                .AppendAsync(
                    new LedgerStream<ProbeEntry>("blocked", "test.probe"),
                    "entry",
                    new ProbeEntry("value", 1)
                );

        await append.Should().ThrowAsync<SqliteException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TerminalRunTransitions_AreIdempotentAndConflictingStatusesFail()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "test");

        var ready = await store.CompleteRunAsync(runId, LedgerRunStatus.Ready);
        var replay = await store.CompleteRunAsync(runId, LedgerRunStatus.Ready);

        replay.Should().Be(ready);
        ready.EndedAt.Should().NotBeNull();
        var conflict = async () => await store.CompleteRunAsync(runId, LedgerRunStatus.Failed);
        await conflict.Should().ThrowAsync<LedgerConflictException>();
    }

    [Fact]
    public async Task AbandonedRun_RemainsRunningWithReadableFactsAndNoResumeApi()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var store = await CreateStoreAsync(path);
        await store.CreateRunAsync(runId, "delivery");
        var facts = new LedgerStream<ProbeEntry>("facts", "test.fact");
        await store.ForRun(runId).AppendAsync(facts, "accepted", new ProbeEntry("durable", 1));

        var reopened = await CreateStoreAsync(path);
        var run = await reopened.GetRunAsync(runId);
        var entries = await reopened.ForRun(runId).ReadAsync(facts);
        var resumeMethods = typeof(SqliteLedgerStore)
            .Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods())
            .Where(method =>
                method.Name.Contains("Resume", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Reconstruct", StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        run.Status.Should().Be(LedgerRunStatus.Running);
        entries.Should().ContainSingle().Which.Value.Name.Should().Be("durable");
        resumeMethods.Should().BeEmpty();
    }

    [Fact]
    public async Task RuntimeJournal_PersistsAcceptedOutputPayloadIdempotently()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var observer = new SqlitePipelineObserver(store.ForRun(runId));
        var decision = new PlannerDecision(
            PlannerDecisionValue.Proceed,
            "Proceed.",
            [],
            ["README.md"]
        );

        var observation = new PipelineStructuredOutputAccepted(
            runId,
            DeliveryIds.Planner,
            "planner-output-1",
            StandardOutcomeKinds.Success,
            typeof(PlannerDecision).FullName,
            JsonSerializer.SerializeToElement(decision, JsonSerializerOptions.Web)
        );
        await observer.ObserveAsync(observation, CancellationToken.None);
        await observer.ObserveAsync(observation, CancellationToken.None);

        var records = await store
            .ForRun(runId)
            .ReadAsync(
                new LedgerStream<RuntimeJournalRecord>("runtime.journal", "tandem.runtime-journal")
            );
        records.Should().ContainSingle();
        records[0].EntryId.Should().Be("accepted-output--planner-output-1");
        records[0]
            .Value.Payload!.Value.Deserialize<PlannerDecision>(JsonSerializerOptions.Web)
            .Should()
            .BeEquivalentTo(decision);
    }

    [Fact]
    public async Task RuntimeJournal_PersistsCapabilityPayloadIdempotently()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var observer = new SqlitePipelineObserver(store.ForRun(runId));
        var request = new AskPlannerRequest("What next?", "Inspect first.", ["README.md"]);

        var observation = new PipelineCapabilityAccepted(
            runId,
            "executor",
            "invocation-1",
            "capability:ask_planner",
            "ask_planner",
            "accepted-call-1",
            "Inspect first.",
            typeof(AskPlannerRequest).FullName,
            JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web)
        );
        await observer.ObserveAsync(observation, CancellationToken.None);
        await observer.ObserveAsync(observation, CancellationToken.None);

        var records = await store
            .ForRun(runId)
            .ReadAsync(
                new LedgerStream<RuntimeJournalRecord>("runtime.journal", "tandem.runtime-journal")
            );
        records.Should().ContainSingle();
        records[0].EntryId.Should().Be("accepted-capability--accepted-call-1");
        records[0]
            .Value.Payload!.Value.Deserialize<AskPlannerRequest>(JsonSerializerOptions.Web)
            .Should()
            .BeEquivalentTo(request);
    }

    [Fact]
    public async Task DeliveryAdapter_PersistsHumanAndVerificationAcceptanceFacts()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var adapter = new DeliveryLedger(store.ForRun(runId));
        var observer = new SqlitePipelineObserver(store.ForRun(runId));
        var question = new HumanQuestion("Which behavior?", "Decision required.");
        var answer = new HumanAnswer("Use strict behavior.");
        var verification = new VerificationResult(
            0,
            "task check",
            0,
            "passed",
            "",
            TimeSpan.FromSeconds(2),
            false
        );

        var requested = new PipelineInteractionRequested<HumanQuestion>(
            runId,
            "PlannerHumanInput",
            "request-1",
            question,
            JsonSerializer.SerializeToElement(question, JsonSerializerOptions.Web)
        );
        var answered = new PipelineInteractionAnswered<HumanAnswer>(
            runId,
            "PlannerHumanInput",
            "request-1",
            answer,
            JsonSerializer.SerializeToElement(answer, JsonSerializerOptions.Web)
        );
        await observer.ObserveAsync(requested, CancellationToken.None);
        await observer.ObserveAsync(requested, CancellationToken.None);
        await observer.ObserveAsync(answered, CancellationToken.None);
        await observer.ObserveAsync(answered, CancellationToken.None);
        await adapter.AcceptVerificationResultAsync(
            $"{runId:N}--verify--1",
            verification,
            CancellationToken.None
        );

        var ledger = store.ForRun(runId);
        var verificationResults = await ledger.ReadAsync(
            new LedgerStream<VerificationResultRecord>(
                "delivery.verification-results",
                "delivery.verification-result"
            )
        );
        var context = await adapter.ReadContextAsync(
            DeliveryLedgerRole.Reviewer,
            CancellationToken.None
        );
        context.HumanAnswers.Should().ContainSingle().Which.Answer.Should().Be(answer);
        (await ledger.ReadAsync(PipelineJournal.Stream)).Should().HaveCount(2);
        verificationResults.Should().ContainSingle();
        verificationResults[0].Value.Result.Should().Be(verification);

        var conflictingAnswer = async () =>
            await observer.ObserveAsync(
                new PipelineInteractionAnswered<HumanAnswer>(
                    runId,
                    "PlannerHumanInput",
                    "request-1",
                    new HumanAnswer("Use permissive behavior."),
                    JsonSerializer.SerializeToElement(
                        new HumanAnswer("Use permissive behavior."),
                        JsonSerializerOptions.Web
                    )
                ),
                CancellationToken.None
            );
        await conflictingAnswer.Should().ThrowAsync<LedgerConflictException>();
    }

    [Fact]
    public async Task DeliveryAdapter_PersistsCheckpointsAndOwnsCurrentDocuments()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var adapter = new DeliveryLedger(store.ForRun(runId));
        var observer = new SqlitePipelineObserver(store.ForRun(runId));
        var packet = new Packet(
            "Packet",
            "file:///repo",
            "main",
            [new Outcome("outcome", "Deliver it")],
            [],
            [],
            ""
        );
        await adapter.InitializeAsync(packet, CancellationToken.None);
        var checkpoint = new ProgressCheckpointRecord(
            "Progress",
            ["Implemented"],
            [new OutcomeProgress("outcome", "Deliver it", false, [])],
            ["src/File.cs"],
            ["README.md"],
            ["Keep API typed"],
            [],
            "Verify"
        );
        var report = new SubmitReportRequest("Complete", ["outcome"], ["src/File.cs"]);
        var review = new ReviewDecision(
            ReviewDecisionValue.Accept,
            "Accepted",
            [new ReviewOutcomeAssessment("outcome", true, ["src/File.cs"])],
            []
        );
        var candidate = new PublicationCandidateDocument(
            "candidate-1",
            packet.Repository,
            "/workspace",
            packet.Title,
            "base",
            "candidate"
        );

        await adapter.AcceptCheckpointAsync("checkpoint-1", checkpoint, CancellationToken.None);
        await adapter.AcceptCheckpointAsync("checkpoint-1", checkpoint, CancellationToken.None);
        await observer.ObserveAsync(
            new PipelineCapabilityAccepted(
                runId,
                "executor",
                "invocation-1",
                "capability:submit_report",
                "submit_report",
                "report-1",
                "Report submitted.",
                typeof(SubmitReportRequest).FullName,
                JsonSerializer.SerializeToElement(report, JsonSerializerOptions.Web)
            ),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineStructuredOutputAccepted(
                runId,
                DeliveryIds.Reviewer,
                "review-1",
                StandardOutcomeKinds.Success,
                typeof(ReviewDecision).FullName,
                JsonSerializer.SerializeToElement(review, JsonSerializerOptions.Web)
            ),
            CancellationToken.None
        );
        await adapter.AcceptPublicationCandidateAsync(
            "candidate-1",
            candidate,
            CancellationToken.None
        );
        await adapter.AcceptPublicationCandidateAsync(
            "candidate-1",
            candidate,
            CancellationToken.None
        );
        var ledger = store.ForRun(runId);
        (
            await ledger.ReadAsync(
                new LedgerStream<ProgressCheckpointRecord>(
                    "delivery.progress-checkpoints",
                    "delivery.progress-checkpoint"
                )
            )
        )
            .Should()
            .ContainSingle();
        var context = await adapter.ReadContextAsync(
            DeliveryLedgerRole.Reviewer,
            CancellationToken.None
        );
        context.Outcomes!.Outcomes.Should().ContainSingle().Which.Delivered.Should().BeTrue();
        context.Outcomes.AcceptedDecisionId.Should().Be("review-1");
        context.Report.Should().BeEquivalentTo(report);
        var acceptedCandidate = await ledger.ReadDocumentAsync(
            new LedgerDocument<PublicationCandidateDocument>(
                "delivery.publication-candidate",
                "delivery.publication-candidate"
            )
        );
        acceptedCandidate!.Value.Should().BeEquivalentTo(candidate);
    }

    [Fact]
    public async Task RuntimeJournal_PersistsTypedHooksAndExcludesAssistantText()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var observer = new SqlitePipelineObserver(store.ForRun(runId));

        await observer.RecordRunStartedAsync(CancellationToken.None);
        await observer.ObserveAsync(
            new PipelineStepStarted(runId, "executor"),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineAgentUpdated(runId, "executor", new AgentUpdate.Text("not durable")),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineAgentUpdated(
                runId,
                "executor",
                new AgentUpdate.Reasoning("also not durable")
            ),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineAgentUsage(runId, "executor", 10, 2, 12),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineActionAttempted(
                runId,
                "executor",
                "invocation-1",
                "file_access_write",
                "WorkspaceMutation"
            ),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineActionCompleted(
                runId,
                "executor",
                "invocation-1",
                "file_access_write",
                "WorkspaceMutation",
                "Completed"
            ),
            CancellationToken.None
        );
        await observer.RecordRunCompletedAsync("Ready", CancellationToken.None);

        var records = await store
            .ForRun(runId)
            .ReadAsync(
                new LedgerStream<RuntimeJournalRecord>("runtime.journal", "tandem.runtime-journal")
            );
        records
            .Select(record => record.Value.Kind)
            .Should()
            .Equal(
                RuntimeJournalKind.RunStarted,
                RuntimeJournalKind.StepStarted,
                RuntimeJournalKind.UsageRecorded,
                RuntimeJournalKind.ActionAttempted,
                RuntimeJournalKind.ActionCompleted,
                RuntimeJournalKind.RunCompleted
            );
        records.Select(record => record.Sequence).Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public async Task RuntimeJournal_PersistsAcceptedStateAndFailureEvidence()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "test");
        var observer = new SqlitePipelineObserver(store.ForRun(runId));
        var successPayload = JsonSerializer.SerializeToElement(
            new { value = "accepted elsewhere" }
        );
        var failurePayload = JsonSerializer.SerializeToElement(
            new FailureEvidence("verification failed", "task check")
        );

        await observer.ObserveAsync(
            new PipelineStepCompleted(
                runId,
                "agent",
                new PipelineRunOutcome(
                    StandardOutcomeKinds.Success,
                    "agent",
                    "Succeeded",
                    successPayload,
                    TimeSpan.Zero
                ),
                PipelineAcceptedValue.FromPayload<RunnerState>(successPayload)
            ),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineStepCompleted(
                runId,
                "verify",
                new PipelineRunOutcome(
                    StandardOutcomeKinds.Failed,
                    "verify",
                    "Failed",
                    failurePayload,
                    TimeSpan.Zero
                ),
                PipelineAcceptedValue.FromPayload<FailureEvidence>(failurePayload)
            ),
            CancellationToken.None
        );

        var records = await store.ForRun(runId).ReadAsync(PipelineJournal.Stream);
        records[0].Value.ValueType.Should().Be(typeof(RunnerState).FullName);
        records[0].Value.Payload!.Value.GetRawText().Should().Be(successPayload.GetRawText());
        records[1].Value.Payload!.Value.GetRawText().Should().Be(failurePayload.GetRawText());
    }

    [Fact]
    public async Task PersistentStateStage_PersistsReturnedStateThroughRealLedger()
    {
        var path = DatabasePath();
        var store = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        var stage = new IncrementStage();
        var pipeline = Pipeline.Start(stage, "state-stage").Persist().Build(stage);
        var observer = await store.CreateObserverAsync(runId, pipeline);

        await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(4),
            new PipelineRunOptions(runId, Observer: observer)
        );

        var reopened = await CreateStoreAsync(path);
        var accepted = await reopened.ReadLatestAcceptedAsync<RunnerState>(runId, stage.Id);
        accepted!.Value.Count.Should().Be(5);
    }

    [Fact]
    public async Task PersistentSuccessfulOutcomeStage_PersistsReturnedStateThroughRealLedger()
    {
        var path = DatabasePath();
        var store = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        var stage = new SuccessfulOutcomeStage();
        var pipeline = Pipeline.Start(stage, "outcome-stage").Persist().Build(stage);
        var observer = await store.CreateObserverAsync(runId, pipeline);

        await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(4),
            new PipelineRunOptions(runId, Observer: observer)
        );

        var reopened = await CreateStoreAsync(path);
        var accepted = await reopened.ReadLatestAcceptedAsync<RunnerState>(runId, stage.Id);
        accepted!.Value.Count.Should().Be(6);
    }

    [Fact]
    public async Task SqliteRunOptions_OwnSuccessfulRunLifecycleAndComposeObserver()
    {
        var path = DatabasePath();
        var observations = new List<PipelineObservation>();
        var stage = new IncrementStage();
        var pipeline = Pipeline.Start(stage, "sqlite-run").Persist().Build(stage);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(4),
            new SqlitePipelineRunOptions(path, Observer: new RecordingObserver(observations))
        );

        result.Succeeded.Should().BeTrue();
        observations.Should().ContainSingle(observation => observation is PipelineStepCompleted);
        var reopened = await CreateStoreAsync(path);
        (await reopened.GetRunAsync(result.RunId)).Status.Should().Be(LedgerRunStatus.Ready);
        var accepted = await reopened.ReadLatestAcceptedAsync<RunnerState>(result.RunId, stage.Id);
        accepted!.Value.Count.Should().Be(5);
    }

    [Fact]
    public async Task SqliteRunOptions_MarkDeclaredFailureAsFailed()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var stage = new DeclaredFailureStage();
        var pipeline = Pipeline.Start(stage, "sqlite-failed-run").Build(stage);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerState(4),
            new SqlitePipelineRunOptions(path, runId)
        );

        result.Status.Should().Be(PipelineRunStatus.Failed);
        var reopened = await CreateStoreAsync(path);
        (await reopened.GetRunAsync(runId)).Status.Should().Be(LedgerRunStatus.Failed);
    }

    [Fact]
    public async Task SqliteRunOptions_MarkExecutionExceptionAsFaulted()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var stage = new FaultStage();
        var pipeline = Pipeline.Start(stage, "sqlite-faulted-run").Build(stage);

        var act = async () =>
            await new PipelineRunner().RunAsync(
                pipeline,
                new RunnerState(4),
                new SqlitePipelineRunOptions(path, runId)
            );

        await act.Should().ThrowAsync<PipelineRunException>();
        var reopened = await CreateStoreAsync(path);
        (await reopened.GetRunAsync(runId)).Status.Should().Be(LedgerRunStatus.Faulted);
    }

    [Fact]
    public async Task SqliteRunOptions_MarkCancellationAsCancelled()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var stage = new WaitForeverStage();
        var pipeline = Pipeline.Start(stage, "sqlite-cancelled-run").Build(stage);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () =>
            await new PipelineRunner().RunAsync(
                pipeline,
                new RunnerState(4),
                new SqlitePipelineRunOptions(path, runId),
                cancellation.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        var reopened = await CreateStoreAsync(path);
        (await reopened.GetRunAsync(runId)).Status.Should().Be(LedgerRunStatus.Cancelled);
    }

    [Fact]
    public async Task PersistentStateStage_PersistsRuntimeStateType()
    {
        var path = DatabasePath();
        var store = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "polymorphic-state-stage");
        var stage = new PolymorphicStateStage();
        var pipeline = Pipeline.Start(stage, "polymorphic-state-stage").Persist().Build(stage);

        await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerBaseState(1),
            new PipelineRunOptions(runId, Observer: new SqlitePipelineObserver(store.ForRun(runId)))
        );

        var reopened = await CreateStoreAsync(path);
        var completed = (await reopened.ForRun(runId).ReadAsync(PipelineJournal.Stream))
            .Select(entry => entry.Value)
            .Single(record => record.Kind == RuntimeJournalKind.StepCompleted);
        completed.ValueType.Should().Be(typeof(RunnerDerivedState).FullName);
        completed.Payload!.Value.GetProperty("detail").GetString().Should().Be("persisted");
    }

    [Fact]
    public async Task DeliveryContext_IsRoleSpecificRecentAndExplicitlyTruncated()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var adapter = new DeliveryLedger(store.ForRun(runId));
        var observer = new SqlitePipelineObserver(store.ForRun(runId));
        await adapter.InitializeAsync(
            new Packet(
                "Packet",
                "file:///repo",
                "main",
                [new Outcome("outcome", "Deliver")],
                [],
                [],
                ""
            ),
            CancellationToken.None
        );
        for (var index = 0; index < 7; index++)
        {
            var decision = new PlannerDecision(
                PlannerDecisionValue.Proceed,
                $"Decision {index}",
                [],
                ["README.md"]
            );
            await observer.ObserveAsync(
                new PipelineStructuredOutputAccepted(
                    runId,
                    DeliveryIds.Planner,
                    $"planner-{index}",
                    StandardOutcomeKinds.Success,
                    typeof(PlannerDecision).FullName,
                    JsonSerializer.SerializeToElement(decision, JsonSerializerOptions.Web)
                ),
                CancellationToken.None
            );
        }
        var report = new SubmitReportRequest(new string('x', 9_000), ["outcome"], ["README.md"]);
        await observer.ObserveAsync(
            new PipelineCapabilityAccepted(
                runId,
                "executor",
                "invocation",
                "capability:submit_report",
                "submit_report",
                "report",
                "Report submitted.",
                typeof(SubmitReportRequest).FullName,
                JsonSerializer.SerializeToElement(report, JsonSerializerOptions.Web)
            ),
            CancellationToken.None
        );

        var executor = await adapter.ReadContextAsync(
            DeliveryLedgerRole.Executor,
            CancellationToken.None
        );
        var reviewer = await adapter.ReadContextAsync(
            DeliveryLedgerRole.Reviewer,
            CancellationToken.None
        );

        executor
            .PlannerDecisions.Select(decision => decision.Rationale)
            .Should()
            .Equal("Decision 2", "Decision 3", "Decision 4", "Decision 5", "Decision 6");
        executor.Report.Should().BeNull();
        reviewer.Report.Should().NotBeNull();
        var formatted = DeliveryLedgerContextFormatter.Format(reviewer);
        formatted.Should().HaveLength(8_000);
        formatted.Should().EndWith("[durable context truncated]\n</durable-delivery-context>");
    }

    [Fact]
    public async Task PublicObserverAndReader_ReturnLatestAcceptedValueBySequenceAndIsolateScope()
    {
        var path = DatabasePath();
        var store = new SqliteLedgerStore(path);
        var firstRun = Guid.CreateVersion7();
        var secondRun = Guid.CreateVersion7();
        var first = await store.CreateObserverAsync(firstRun, "first");
        var second = await store.CreateObserverAsync(secondRun, "second");
        await first.ObserveAsync(AcceptedStep(firstRun, "shared", new RunnerState(1)), default);
        await first.ObserveAsync(AcceptedStep(firstRun, "other", new RunnerState(9)), default);
        await first.ObserveAsync(AcceptedStep(firstRun, "shared", new RunnerState(2)), default);
        await second.ObserveAsync(AcceptedStep(secondRun, "shared", new RunnerState(7)), default);

        var reopened = new SqliteLedgerStore(path);
        await reopened.InitializeAsync();
        var latest = await reopened.ReadLatestAcceptedAsync<RunnerState>(firstRun, "shared");
        var other = await reopened.ReadLatestAcceptedAsync<RunnerState>(firstRun, "other");
        var isolated = await reopened.ReadLatestAcceptedAsync<RunnerState>(secondRun, "shared");

        latest!.Value.Count.Should().Be(2);
        latest.Sequence.Should().BeGreaterThan(other!.Sequence);
        other.Value.Count.Should().Be(9);
        isolated!.Value.Count.Should().Be(7);
    }

    [Fact]
    public async Task ReopenedObservers_ContinueJournalSequenceWithoutIdentityCollisions()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var firstStore = new SqliteLedgerStore(path);
        var first = await firstStore.CreateObserverAsync(runId, "pipeline");
        await first.ObserveAsync(new PipelineStepStarted(runId, "first"), default);

        var reopenedStore = new SqliteLedgerStore(path);
        var reopened = await reopenedStore.CreateObserverAsync(runId, "pipeline");
        await reopened.ObserveAsync(new PipelineStepStarted(runId, "second"), default);
        await Task.WhenAll(
            first.ObserveAsync(new PipelineStepStarted(runId, "third"), default).AsTask(),
            reopened.ObserveAsync(new PipelineStepStarted(runId, "fourth"), default).AsTask()
        );

        var entries = await reopenedStore.ForRun(runId).ReadAsync(PipelineJournal.Stream);
        entries.Select(entry => entry.Sequence).Should().Equal(1, 2, 3, 4);
        entries.Select(entry => entry.EntryId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ObserverFactory_RejectsCompositionConflictsAndTerminalRuns()
    {
        var store = new SqliteLedgerStore(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "pipeline");
        var conflict = async () => await store.CreateObserverAsync(runId, "other");
        await conflict.Should().ThrowAsync<LedgerConflictException>();
        var mismatchedObservation = async () =>
            await observer.ObserveAsync(
                new PipelineStepStarted(Guid.CreateVersion7(), "wrong-run"),
                default
            );
        await mismatchedObservation.Should().ThrowAsync<LedgerConflictException>();
        await store.CompleteRunAsync(runId, LedgerRunStatus.Ready);

        var reopen = async () => await store.CreateObserverAsync(runId, "pipeline");
        var append = async () =>
            await observer.ObserveAsync(new PipelineStepStarted(runId, "late"), default);
        var directAppend = async () =>
            await store
                .ForRun(runId)
                .AppendAsync(new LedgerStream<string>("terminal", "test.terminal"), "late", "late");
        var write = async () =>
            await store
                .ForRun(runId)
                .WriteDocumentAsync(
                    new LedgerDocument<RunnerState>("state", "test.state"),
                    new RunnerState(1),
                    0
                );

        await reopen.Should().ThrowAsync<LedgerConflictException>();
        await append.Should().ThrowAsync<LedgerConflictException>();
        await directAppend.Should().ThrowAsync<LedgerConflictException>();
        await write.Should().ThrowAsync<LedgerConflictException>();
    }

    [Fact]
    public async Task JournalCompletionAndTerminalStatus_CommitAtomically()
    {
        var store = new SqliteLedgerStore(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "pipeline");

        await store.ExecuteAsync(async cancellationToken =>
        {
            await observer.RecordRunCompletedAsync("Ready", cancellationToken);
            await store.CompleteRunAsync(runId, LedgerRunStatus.Ready, cancellationToken);
            return true;
        });

        (await store.GetRunAsync(runId)).Status.Should().Be(LedgerRunStatus.Ready);
        var journal = await store.ForRun(runId).ReadAsync(PipelineJournal.Stream);
        journal
            .Should()
            .ContainSingle(entry => entry.Value.Kind == RuntimeJournalKind.RunCompleted);
    }

    [Fact]
    public async Task AcceptedReader_ReturnsExplicitAbsenceAndRejectsTypeMismatchAndMalformedData()
    {
        var store = new SqliteLedgerStore(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "pipeline");
        (await store.ReadLatestAcceptedAsync<RunnerState>(runId, "missing")).Should().BeNull();
        await observer.ObserveAsync(AcceptedStep(runId, "typed", new RunnerState(1)), default);
        await observer.ObserveAsync(
            new PipelineStepCompleted(
                runId,
                "malformed",
                new PipelineRunOutcome(
                    StandardOutcomeKinds.Success,
                    "malformed",
                    "Succeeded",
                    default,
                    TimeSpan.Zero
                ),
                new PipelineAcceptedValue(
                    typeof(RunnerState).FullName!,
                    JsonSerializer.SerializeToElement(new { count = "not-an-integer" })
                )
            ),
            default
        );
        await observer.ObserveAsync(
            new PipelineStepCompleted(
                runId,
                "null",
                new PipelineRunOutcome(
                    StandardOutcomeKinds.Success,
                    "null",
                    "Succeeded",
                    default,
                    TimeSpan.Zero
                ),
                new PipelineAcceptedValue(
                    typeof(RunnerState).FullName!,
                    JsonSerializer.SerializeToElement<RunnerState?>(null)
                )
            ),
            default
        );

        var mismatch = async () =>
            await store.ReadLatestAcceptedAsync<RunnerDerivedState>(runId, "typed");
        var malformed = async () =>
            await store.ReadLatestAcceptedAsync<RunnerState>(runId, "malformed");
        var nullValue = async () => await store.ReadLatestAcceptedAsync<RunnerState>(runId, "null");

        await mismatch.Should().ThrowAsync<LedgerValueTypeMismatchException>();
        await malformed.Should().ThrowAsync<LedgerDataException>();
        await nullValue.Should().ThrowAsync<LedgerDataException>();
    }

    [Fact]
    public async Task UnknownSchemaVersion_FailsWithoutRewritingTheDatabase()
    {
        var path = DatabasePath();
        Directory.CreateDirectory(_directory);
        await using (
            var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}")
        )
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteLedgerStore(path);

        var initialize = async () => await store.InitializeAsync();

        await initialize.Should().ThrowAsync<InvalidOperationException>().WithMessage("*99*");
    }

    [Fact]
    public async Task RelationalConstraintsRejectInvalidRows()
    {
        var path = DatabasePath();
        var store = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "test");
        var id = runId.ToString("N");
        var invalidStatements = new[]
        {
            $"INSERT INTO runs VALUES ('{Guid.NewGuid():N}', '', 'Running', 0, 0, NULL);",
            $"INSERT INTO runs VALUES ('{Guid.NewGuid():N}', 'test', 'Unknown', 0, 0, NULL);",
            "INSERT INTO run_entries VALUES ('missing', 'stream', 1, 'entry', X'00', X'00', 0);",
            $"INSERT INTO run_entries VALUES ('{id}', '', 1, 'entry-1', X'00', X'00', 0);",
            $"INSERT INTO run_entries VALUES ('{id}', 'stream', 0, 'entry-2', X'00', X'00', 0);",
            $"INSERT INTO run_entries VALUES ('{id}', 'stream', 1, '', X'00', X'00', 0);",
            $"INSERT INTO run_documents VALUES ('{id}', '', 1, X'00', X'00', 0);",
            $"INSERT INTO run_documents VALUES ('{id}', 'document', 0, X'00', X'00', 0);",
        };

        foreach (var sql in invalidStatements)
        {
            var execute = async () => await ExecuteSqlAsync(path, sql);
            await execute.Should().ThrowAsync<SqliteException>();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string DatabasePath()
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "ledger.sqlite3");
    }

    private static async ValueTask<SqliteLedgerStore> CreateStoreAsync(string path)
    {
        var store = new SqliteLedgerStore(path);
        await store.InitializeAsync();
        return store;
    }

    private static PipelineStepCompleted AcceptedStep(
        Guid runId,
        string stepId,
        RunnerState state
    ) =>
        new(
            runId,
            stepId,
            new PipelineRunOutcome(
                StandardOutcomeKinds.Success,
                stepId,
                "Succeeded",
                default,
                TimeSpan.Zero
            ),
            new PipelineAcceptedValue(
                typeof(RunnerState).FullName!,
                JsonSerializer.SerializeToElement(state, JsonSerializerOptions.Web)
            )
        );

    private static async Task<WorkerResult> RunWorkerAsync(
        string databasePath,
        Guid runId,
        string entryId,
        string value
    )
    {
        var worker = Path.GetFullPath(
            "../../../../Tandem.Ledger.TestWorker/bin/Debug/net10.0/Tandem.Ledger.TestWorker.dll",
            AppContext.BaseDirectory
        );
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add(worker);
        process.StartInfo.ArgumentList.Add(databasePath);
        process.StartInfo.ArgumentList.Add(runId.ToString("D"));
        process.StartInfo.ArgumentList.Add(entryId);
        process.StartInfo.ArgumentList.Add(value);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new WorkerResult(process.ExitCode, output.Trim(), error.Trim());
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False"
        );
        await connection.OpenAsync();
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ProbeEntry(string Name, int Count);

    private sealed record ProcessEntry(string Value);

    private sealed record WorkerResult(int ExitCode, string Output, string Error);

    private sealed class RecordingObserver(List<PipelineObservation> observations)
        : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            observations.Add(observation);
            return ValueTask.CompletedTask;
        }
    }
}
