using System.Diagnostics;
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
    public async Task DeliveryAdapter_PersistsTypedDecisionsAndReplaysAcceptedOutputIdentity()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var adapter = new DeliveryLedger(store.ForRun(runId));
        var decision = new PlannerDecision(
            PlannerDecisionValue.Proceed,
            "Proceed.",
            [],
            ["README.md"]
        );

        await adapter.AcceptPlannerDecisionAsync(
            "planner-output-1",
            decision,
            CancellationToken.None
        );
        await adapter.AcceptPlannerDecisionAsync(
            "planner-output-1",
            decision,
            CancellationToken.None
        );

        var records = await store
            .ForRun(runId)
            .ReadAsync(
                new LedgerStream<PlannerDecisionRecord>(
                    "delivery.planner-decisions",
                    "delivery.planner-decision"
                )
            );
        records.Should().ContainSingle();
        records[0].EntryId.Should().Be("planner-output-1");
        records[0].Value.Decision.Should().BeEquivalentTo(decision);
    }

    [Fact]
    public async Task DeliveryAdapter_PersistsTypedCapabilityAcceptanceIdempotently()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var adapter = new DeliveryLedger(store.ForRun(runId));
        var request = new AskPlannerRequest("What next?", "Inspect first.", ["README.md"]);

        await adapter.AcceptCapabilityAsync(
            "accepted-call-1",
            "ask_planner",
            request,
            CancellationToken.None
        );
        await adapter.AcceptCapabilityAsync(
            "accepted-call-1",
            "ask_planner",
            request,
            CancellationToken.None
        );

        var records = await store
            .ForRun(runId)
            .ReadAsync(
                new LedgerStream<CapabilityAcceptedRecord<AskPlannerRequest>>(
                    "delivery.capabilities.ask_planner",
                    "delivery.capability.ask_planner"
                )
            );
        records.Should().ContainSingle();
        records[0].EntryId.Should().Be("accepted-call-1");
        records[0].Value.Request.Should().BeEquivalentTo(request);
    }

    [Fact]
    public async Task DeliveryAdapter_PersistsHumanAndVerificationAcceptanceFacts()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var adapter = new DeliveryLedger(store.ForRun(runId));
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

        await adapter.AcceptHumanAnswerAsync(
            "request-1",
            "PlannerHumanInput",
            question,
            answer,
            CancellationToken.None
        );
        await adapter.AcceptVerificationResultAsync(
            $"{runId:N}--verify--1",
            verification,
            CancellationToken.None
        );

        var ledger = store.ForRun(runId);
        var answers = await ledger.ReadAsync(
            new LedgerStream<HumanAnswerRecord>("delivery.human-answers", "delivery.human-answer")
        );
        var verificationResults = await ledger.ReadAsync(
            new LedgerStream<VerificationResultRecord>(
                "delivery.verification-results",
                "delivery.verification-result"
            )
        );
        answers.Should().ContainSingle();
        answers[0].Value.Answer.Should().Be(answer);
        verificationResults.Should().ContainSingle();
        verificationResults[0].Value.Result.Should().Be(verification);

        var conflictingAnswer = async () =>
            await adapter.AcceptHumanAnswerAsync(
                "request-1",
                "PlannerHumanInput",
                question,
                new HumanAnswer("Use permissive behavior."),
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
        await adapter.AcceptReportAsync("report-1", report, CancellationToken.None);
        await adapter.AcceptReportAsync("report-1", report, CancellationToken.None);
        await adapter.AcceptReviewDecisionAsync("review-1", review, CancellationToken.None);
        await adapter.AcceptReviewDecisionAsync("review-1", review, CancellationToken.None);
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
        await adapter.AcceptTerminalOutcomeAsync(
            "terminal--Ready",
            new TerminalOutcomeRecord("Ready", "candidate", null),
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
        var outcomes = await ledger.ReadDocumentAsync(
            new LedgerDocument<OutcomeProgressDocument>(
                "delivery.outcomes",
                "delivery.outcome-progress"
            )
        );
        outcomes!.Value.Outcomes.Should().ContainSingle().Which.Delivered.Should().BeTrue();
        outcomes.Value.AcceptedDecisionId.Should().Be("review-1");
        var acceptedReport = await ledger.ReadDocumentAsync(
            new LedgerDocument<AcceptedImplementationReportDocument>(
                "delivery.implementation-report",
                "delivery.implementation-report"
            )
        );
        acceptedReport!.Value.Report.Should().BeEquivalentTo(report);
        var acceptedCandidate = await ledger.ReadDocumentAsync(
            new LedgerDocument<PublicationCandidateDocument>(
                "delivery.publication-candidate",
                "delivery.publication-candidate"
            )
        );
        acceptedCandidate!.Value.Should().BeEquivalentTo(candidate);
        (
            await ledger.ReadAsync(
                new LedgerStream<TerminalOutcomeRecord>(
                    "delivery.terminal-outcomes",
                    "delivery.terminal-outcome"
                )
            )
        )
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task RuntimeJournal_PersistsTypedHooksAndExcludesAssistantText()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var observer = new LedgerPipelineObserver(store.ForRun(runId));

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
    public async Task DeliveryContext_IsRoleSpecificRecentAndExplicitlyTruncated()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        var adapter = new DeliveryLedger(store.ForRun(runId));
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
            await adapter.AcceptPlannerDecisionAsync(
                $"planner-{index}",
                new PlannerDecision(
                    PlannerDecisionValue.Proceed,
                    $"Decision {index}",
                    [],
                    ["README.md"]
                ),
                CancellationToken.None
            );
        }
        await adapter.AcceptReportAsync(
            "report",
            new SubmitReportRequest(new string('x', 9_000), ["outcome"], ["README.md"]),
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
            .PlannerDecisions.Select(record => record.Decision.Rationale)
            .Should()
            .Equal("Decision 2", "Decision 3", "Decision 4", "Decision 5", "Decision 6");
        executor.Report.Should().BeNull();
        reviewer.Report.Should().NotBeNull();
        var formatted = DeliveryLedgerContextFormatter.Format(reviewer);
        formatted.Should().HaveLength(8_000);
        formatted.Should().EndWith("[durable context truncated]\n</durable-delivery-context>");
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
}
