using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class SqliteLedgerStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    [Fact]
    public async Task Initialize_CreatesMissingDatabaseParentDirectory()
    {
        var path = _directory.Combine("nested", "ledger.sqlite3");
        var store = new SqliteLedgerStore(path);

        await store.InitializeAsync();

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Journal_IsOrderedAndDurableAcrossStoreInstances()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var observer = await new SqliteLedgerStore(path).CreateObserverAsync(runId, "test");

        await observer.ObserveAsync(new PipelineStepStarted(runId, "first"), default);
        await observer.ObserveAsync(new PipelineStepStarted(runId, "second"), default);

        var entries = await new SqliteLedgerStore(path).ReadJournalAsync(runId);
        entries.Select(entry => entry.Record.StepId).Should().Equal("first", "second");
        entries.Select(entry => entry.Sequence).Should().Equal(1, 2);
    }

    [Fact]
    public async Task AgentLedgerReader_PaginatesAndSearchesAcceptedRecords()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "test");
        await observer.ObserveAsync(AcceptedStep(runId, "alpha", new RunnerState(1)), default);
        await observer.ObserveAsync(AcceptedStep(runId, "needle", new RunnerState(2)), default);
        await observer.ObserveAsync(AcceptedStep(runId, "omega", new RunnerState(3)), default);

        var reader = (IPipelineLedgerReader)store.ForRun(runId);
        var firstPage = await reader.ReadAsync(limit: 2);
        var secondPage = await reader.ReadAsync(firstPage.NextCursor, limit: 2);
        var search = await reader.SearchAsync("NEEDLE");

        firstPage.Entries.Select(entry => entry.Sequence).Should().Equal(1, 2);
        firstPage.NextCursor.Should().NotBeNull();
        secondPage.Entries.Select(entry => entry.Sequence).Should().Equal(3);
        secondPage.NextCursor.Should().BeNull();
        search.Entries.Should().ContainSingle().Which.Sequence.Should().Be(2);
        search.Entries[0].Value.Should().Contain("needle");
    }

    [Fact]
    public async Task AgentLedgerReader_SearchMatchesValuesNotPropertyNames()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "test");
        await observer.ObserveAsync(AcceptedStep(runId, "planner", new RunnerState(7)), default);
        await observer.ObserveAsync(
            new PipelineCommandOutput(runId, "verify", "task check", "stepId is not a key here", 1),
            default
        );

        var reader = (IPipelineLedgerReader)store.ForRun(runId);
        var propertyName = await reader.SearchAsync("valueType");
        var nestedPropertyName = await reader.SearchAsync("count");
        var value = await reader.SearchAsync("PLANNER");
        var nestedValue = await reader.SearchAsync("7");
        var outputValue = await reader.SearchAsync("stepId");

        propertyName.Entries.Should().BeEmpty();
        nestedPropertyName.Entries.Should().BeEmpty();
        value.Entries.Should().ContainSingle().Which.Sequence.Should().Be(1);
        nestedValue.Entries.Should().ContainSingle().Which.Sequence.Should().Be(1);
        outputValue.Entries.Should().ContainSingle().Which.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task AgentLedgerReader_BoundsValuesAndKeepsSearchMatchVisible()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "test");
        var marker = "MATCH-NEAR-THE-END";
        await observer.ObserveAsync(
            new PipelineCommandOutput(
                runId,
                "large",
                "task check",
                new string('x', 20_000) + marker,
                1
            ),
            default
        );

        var reader = (IPipelineLedgerReader)store.ForRun(runId);
        var read = await reader.ReadAsync();
        var search = await reader.SearchAsync(marker);

        read.Entries.Single().Value.Length.Should().BeLessThan(5_000);
        read.Entries.Single().Value.Should().Contain("[...truncated...]");
        search.Entries.Single().Value.Should().Contain(marker);
        search.Entries.Single().Value.Should().StartWith("[...truncated...]");
    }

    [Fact]
    public async Task AgentLedgerReader_HidesRuntimeMechanicsAndReturnsAcceptedFacts()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "test", CancellationToken.None);
        await observer.RecordRunStartedAsync(CancellationToken.None);
        await observer.ObserveAsync(
            new PipelineAgentUsage(runId, "agent", 10, 2, 12, 100),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineStepCompleted(
                runId,
                "ephemeral",
                new PipelineRunOutcome(
                    StandardOutcomeKinds.Success,
                    "ephemeral",
                    "Not persisted.",
                    JsonSerializer.SerializeToElement(new { }),
                    TimeSpan.Zero
                )
            ),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            AcceptedStep(runId, "agent", new RunnerState(3)),
            CancellationToken.None
        );

        var page = await ((IPipelineLedgerReader)store.ForRun(runId)).ReadAsync(
            cancellationToken: CancellationToken.None
        );

        page.Entries.Should().ContainSingle();
        page.Entries.Single().Value.Should().Contain("StepCompleted");
        page.Entries.Single().Value.Should().NotContain("RunStarted");
        page.Entries.Single().Value.Should().NotContain("UsageRecorded");
    }

    [Fact]
    public async Task AgentLedgerReader_ReturnsPersistedCommandOutput()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "test", CancellationToken.None);
        await observer.ObserveAsync(
            new PipelineCommandOutput(
                runId,
                "executor",
                "task ui:e2e:ci",
                "FORM_FIELD_OPTIONS_REQUIRED",
                1
            ),
            CancellationToken.None
        );
        await store.AppendAsync(
            runId,
            new RuntimeJournalRecord(
                RuntimeJournalKind.CommandCompleted,
                "executor",
                Name: "command without output",
                Result: "1"
            ),
            CancellationToken.None
        );

        var reader = (IPipelineLedgerReader)store.ForRun(runId);
        var page = await reader.ReadAsync(cancellationToken: CancellationToken.None);
        var search = await reader.SearchAsync(
            "FORM_FIELD_OPTIONS_REQUIRED",
            cancellationToken: CancellationToken.None
        );

        page.Entries.Should().ContainSingle();
        page.Entries[0].Value.Should().Contain("CommandCompleted");
        page.Entries[0].Value.Should().Contain("task ui:e2e:ci");
        page.Entries[0].Value.Should().Contain("FORM_FIELD_OPTIONS_REQUIRED");
        page.Entries[0].Value.Should().Contain("\"result\":\"1\"");
        search.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task ConcurrentAppends_AreSerializedBySQLiteWithContiguousSequences()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        await new SqliteLedgerStore(path).CreateObserverAsync(runId, "test");

        await Task.WhenAll(
            Enumerable
                .Range(0, 24)
                .Select(async index =>
                {
                    var observer = await new SqliteLedgerStore(path).CreateObserverAsync(
                        runId,
                        "test"
                    );
                    await observer.ObserveAsync(
                        new PipelineStepStarted(runId, $"step-{index}"),
                        default
                    );
                })
        );

        var entries = await new SqliteLedgerStore(path).ReadJournalAsync(runId);
        entries.Should().HaveCount(24);
        entries
            .Select(entry => entry.Sequence)
            .Should()
            .Equal(Enumerable.Range(1, 24).Select(i => (long)i));
    }

    [Fact]
    public async Task SeparateProcesses_AllocateContiguousSequences()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index => RunWorkerAsync(path, runId, $"step-{index}"))
        );

        results.Should().OnlyContain(result => result.ExitCode == 0);
        var entries = await new SqliteLedgerStore(path).ReadJournalAsync(runId);
        entries
            .Select(entry => entry.Sequence)
            .Should()
            .Equal(Enumerable.Range(1, 8).Select(i => (long)i));
        entries
            .Select(entry => entry.Record.StepId)
            .Should()
            .BeEquivalentTo(Enumerable.Range(0, 8).Select(index => $"step-{index}"));
    }

    [Fact]
    public async Task WalReader_DoesNotBlockAnotherStoreFromAppending()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var setup = await new SqliteLedgerStore(path).CreateObserverAsync(runId, "test");
        await setup.ObserveAsync(new PipelineStepStarted(runId, "first"), default);

        await using var reader = new SqliteConnection(
            $"Data Source={path};Cache=Shared;Pooling=False"
        );
        await reader.OpenAsync();
        await using var snapshot = reader.BeginTransaction(deferred: true);
        await using var command = reader.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = "SELECT COUNT(*) FROM journal;";
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(1);

        var writer = new SqliteLedgerStore(path);
        await writer.AppendAsync(
            runId,
            new RuntimeJournalRecord(RuntimeJournalKind.StepStarted, "second"),
            default
        );

        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(1);
        await snapshot.CommitAsync();
        (await writer.ReadJournalAsync(runId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task RunsRemainIsolated()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var firstRun = Guid.CreateVersion7();
        var secondRun = Guid.CreateVersion7();
        var first = await store.CreateObserverAsync(firstRun, "test");
        var second = await store.CreateObserverAsync(secondRun, "test");
        await first.ObserveAsync(new PipelineStepStarted(firstRun, "first"), default);
        await second.ObserveAsync(new PipelineStepStarted(secondRun, "second"), default);

        (await store.ReadJournalAsync(firstRun)).Single().Record.StepId.Should().Be("first");
        (await store.ReadJournalAsync(secondRun)).Single().Record.StepId.Should().Be("second");
        (await store.ReadJournalAsync(secondRun)).Single().Sequence.Should().Be(1);
    }

    [Fact]
    public async Task Append_WaitsForAnotherWriterAndFollowsItsCommit()
    {
        var path = DatabasePath();
        var store = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "test", default);
        await using var blocker = new SqliteConnection($"Data Source={path};Pooling=False");
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(deferred: false);
        await using (var insert = blocker.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                $"INSERT INTO journal (run_id, sequence, record, recorded_at) VALUES ('{runId:N}', 1, '{{\"kind\":\"StepStarted\",\"stepId\":\"blocker\"}}', 0);";
            await insert.ExecuteNonQueryAsync();
        }

        // Microsoft.Data.Sqlite waits for the lock synchronously, so the writer needs its own thread.
        var append = Task.Run(async () =>
            await store.AppendAsync(
                runId,
                new RuntimeJournalRecord(RuntimeJournalKind.StepStarted, "waiting"),
                default
            )
        );
        (await Task.WhenAny(append, Task.Delay(TimeSpan.FromMilliseconds(200))))
            .Should()
            .NotBeSameAs(append, "the writer lock is still held");
        await transaction.CommitAsync();

        (await append).Should().Be(2);
        (await store.ReadJournalAsync(runId))
            .Select(entry => entry.Record.StepId)
            .Should()
            .Equal("blocker", "waiting");
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

    [Theory]
    [InlineData(LedgerRunStatus.Ready)]
    [InlineData(LedgerRunStatus.Failed)]
    [InlineData(LedgerRunStatus.Faulted)]
    [InlineData(LedgerRunStatus.Interrupted)]
    [InlineData(LedgerRunStatus.Cancelled)]
    public async Task TerminalRun_KeepsItsJournalReadable(LedgerRunStatus status)
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var store = await CreateStoreAsync(path);
        var observer = await store.CreateObserverAsync(runId, "delivery");
        await observer.ObserveAsync(AcceptedStep(runId, "agent", new RunnerState(1)), default);
        await store.CompleteRunAsync(runId, status);

        var reopened = await CreateStoreAsync(path);

        (await reopened.GetRunAsync(runId)).Status.Should().Be(status);
        (await reopened.ReadLatestAcceptedAsync<RunnerState>(runId, "agent"))!
            .Value.Count.Should()
            .Be(1);
    }

    [Fact]
    public async Task RuntimeJournal_PersistsAcceptedOutputPayload()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "delivery");
        var decision = new ProbeDecision("Proceed.", ["README.md"]);

        await observer.ObserveAsync(
            new PipelineStructuredOutputAccepted(
                runId,
                "planner",
                "planner-output-1",
                StandardOutcomeKinds.Success,
                typeof(ProbeDecision).FullName,
                JsonSerializer.SerializeToElement(decision, JsonSerializerOptions.Web)
            ),
            CancellationToken.None
        );

        var record = (await store.ReadJournalAsync(runId)).Should().ContainSingle().Subject.Record;
        record.Kind.Should().Be(RuntimeJournalKind.StructuredOutputAccepted);
        record.Identity.Should().Be("planner-output-1");
        record
            .Payload!.Value.Deserialize<ProbeDecision>(JsonSerializerOptions.Web)
            .Should()
            .BeEquivalentTo(decision);
    }

    [Fact]
    public async Task RuntimeJournal_PersistsStructuredOutputRejectionsWithoutAcceptingThem()
    {
        var path = DatabasePath();
        var store = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "delivery");
        var first = new PipelineStructuredOutputRejected(
            runId,
            "planner",
            1,
            [new ValidationProblem("$.decision", "decision is required")],
            "{\"decision\":null}"
        );
        var second = new PipelineStructuredOutputRejected(
            runId,
            "planner",
            2,
            [new ValidationProblem("$.reason", "reason is required")],
            "{\"decision\":\"proceed\"}"
        );

        await observer.ObserveAsync(first, CancellationToken.None);
        await observer.ObserveAsync(second, CancellationToken.None);

        var reopened = await CreateStoreAsync(path);
        var records = (await reopened.ReadJournalAsync(runId))
            .Select(entry => entry.Record)
            .ToList();
        records
            .Select(record => record.Kind)
            .Should()
            .OnlyContain(kind => kind == RuntimeJournalKind.StructuredOutputRejected);
        records.Select(record => record.Identity).Should().Equal("1", "2");
        records
            .Select(record =>
                record.Payload!.Value.Deserialize<StructuredOutputRejectionEvidence>(
                    TandemJson.CreateTypedContract()
                )!
            )
            .Should()
            .BeEquivalentTo(
                [
                    new StructuredOutputRejectionEvidence(1, first.Problems, first.RawResponse),
                    new StructuredOutputRejectionEvidence(2, second.Problems, second.RawResponse),
                ],
                options => options.WithStrictOrdering()
            );
        (
            await reopened.ReadLatestAcceptedAsync<StructuredOutputRejectionEvidence>(
                runId,
                "planner"
            )
        )
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task RuntimeJournal_PersistsCapabilityPayload()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "delivery");
        var request = new ProbeRequest("What next?", "Inspect first.", ["README.md"]);

        await observer.ObserveAsync(
            new PipelineCapabilityAccepted(
                runId,
                "executor",
                "invocation-1",
                "capability:ask_planner",
                "ask_planner",
                "accepted-call-1",
                "Inspect first.",
                typeof(ProbeRequest).FullName,
                JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web)
            ),
            CancellationToken.None
        );

        var record = (await store.ReadJournalAsync(runId)).Should().ContainSingle().Subject.Record;
        record.Kind.Should().Be(RuntimeJournalKind.CapabilityAccepted);
        record.Identity.Should().Be("accepted-call-1");
        record
            .Payload!.Value.Deserialize<ProbeRequest>(JsonSerializerOptions.Web)
            .Should()
            .BeEquivalentTo(request);
    }

    [Fact]
    public async Task RuntimeJournal_PersistsTypedHooksAndExcludesAssistantText()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "delivery");

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
            new PipelineAgentUsage(runId, "executor", 10, 2, 12, 200_000),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineActionAttempted(
                runId,
                "executor",
                "invocation-1",
                "file_access_write",
                ToolEffect.WorkspaceMutation
            ),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineActionCompleted(
                runId,
                "executor",
                "invocation-1",
                "file_access_write",
                ToolEffect.WorkspaceMutation,
                ToolInvocationStatus.Completed
            ),
            CancellationToken.None
        );
        await observer.ObserveAsync(
            new PipelineCommandOutput(runId, "executor", "task check", "red output", 7),
            CancellationToken.None
        );
        await observer.RecordRunCompletedAsync("Ready", CancellationToken.None);

        var records = await store.ReadJournalAsync(runId);
        records
            .Select(record => record.Record.Kind)
            .Should()
            .Equal(
                RuntimeJournalKind.RunStarted,
                RuntimeJournalKind.StepStarted,
                RuntimeJournalKind.UsageRecorded,
                RuntimeJournalKind.ActionAttempted,
                RuntimeJournalKind.ActionCompleted,
                RuntimeJournalKind.CommandCompleted,
                RuntimeJournalKind.RunCompleted
            );
        records.Select(record => record.Sequence).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        records
            .Single(record => record.Record.Kind == RuntimeJournalKind.UsageRecorded)
            .Record.ContextWindowTokens.Should()
            .Be(200_000);
        var command = records
            .Single(record => record.Record.Kind == RuntimeJournalKind.CommandCompleted)
            .Record;
        command.Name.Should().Be("task check");
        command.Result.Should().Be("7");
        command.Payload!.Value.GetString().Should().Be("red output");
    }

    [Fact]
    public async Task RuntimeJournal_StoresUnknownContextWindowAsAbsent()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "usage-without-window");

        await observer.ObserveAsync(
            new PipelineAgentUsage(runId, "executor", 10, 2, 12),
            CancellationToken.None
        );

        var records = await store.ReadJournalAsync(runId);
        records.Single().Record.ContextWindowTokens.Should().BeNull();
    }

    [Fact]
    public async Task RuntimeJournal_PersistsAcceptedStateAndFailureEvidence()
    {
        var store = await CreateStoreAsync(DatabasePath());
        var runId = Guid.CreateVersion7();
        var observer = await store.CreateObserverAsync(runId, "test");
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

        var records = await store.ReadJournalAsync(runId);
        records[0].Record.ValueType.Should().Be(typeof(RunnerState).FullName);
        JsonElement.DeepEquals(records[0].Record.Payload!.Value, successPayload).Should().BeTrue();
        JsonElement.DeepEquals(records[1].Record.Payload!.Value, failurePayload).Should().BeTrue();
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
    public async Task SqliteRunOptions_PreservesCancellationWhenItFiresDuringRunCreation()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var stage = new WaitForeverStage();
        var pipeline = Pipeline.Start(stage, "sqlite-cancelled-startup").Build(stage);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        // The store deliberately initializes slowly so the cancellation fires while the
        // run row is still being created; the run must still exist and be Cancelled.
        await new SqliteLedgerStore(path).InitializeAsync();

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
    public async Task SqliteRunOptions_SurfaceCancellationTerminalizationFailure()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var stage = new WaitForeverStage();
        var pipeline = Pipeline.Start(stage, "sqlite-cancelled-run").Build(stage);
        using var cancellation = new CancellationTokenSource();

        var act = async () =>
            await new PipelineRunner().RunAsync(
                pipeline,
                new RunnerState(4),
                new SqlitePipelineRunOptions(
                    path,
                    runId,
                    Observer: new BreakLedgerAfterCancellationObserver(path, cancellation)
                ),
                cancellation.Token
            );

        var failure = await act.Should().ThrowAsync<AggregateException>();
        failure
            .Which.InnerExceptions.Should()
            .Contain(error => error is OperationCanceledException);
        failure.Which.InnerExceptions.Should().Contain(error => error is SqliteException);
    }

    [Fact]
    public async Task PersistentStateStage_PersistsRuntimeStateType()
    {
        var path = DatabasePath();
        var store = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        var stage = new PolymorphicStateStage();
        var pipeline = Pipeline.Start(stage, "polymorphic-state-stage").Persist().Build(stage);
        var observer = await store.CreateObserverAsync(runId, pipeline);

        await new PipelineRunner().RunAsync(
            pipeline,
            new RunnerBaseState(1),
            new PipelineRunOptions(runId, Observer: observer)
        );

        var reopened = await CreateStoreAsync(path);
        var completed = (await reopened.ReadJournalAsync(runId))
            .Select(entry => entry.Record)
            .Single(record => record.Kind == RuntimeJournalKind.StepCompleted);
        completed.ValueType.Should().Be(typeof(RunnerDerivedState).FullName);
        completed.Payload!.Value.GetProperty("detail").GetString().Should().Be("persisted");
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
        var runLatest = await reopened.ReadLatestAcceptedAsync<RunnerState>(firstRun);
        var isolated = await reopened.ReadLatestAcceptedAsync<RunnerState>(secondRun, "shared");

        latest!.Value.Count.Should().Be(2);
        latest.Sequence.Should().BeGreaterThan(other!.Sequence);
        other.Value.Count.Should().Be(9);
        runLatest.Should().Be(latest);
        isolated!.Value.Count.Should().Be(7);
    }

    [Fact]
    public async Task SecondObserverForARunningRun_ContinuesTheJournalSequence()
    {
        var path = DatabasePath();
        var runId = Guid.CreateVersion7();
        var first = await new SqliteLedgerStore(path).CreateObserverAsync(runId, "pipeline");
        await first.ObserveAsync(new PipelineStepStarted(runId, "first"), default);

        var secondStore = new SqliteLedgerStore(path);
        var second = await secondStore.CreateObserverAsync(runId, "pipeline");
        await second.ObserveAsync(new PipelineStepStarted(runId, "second"), default);
        await Task.WhenAll(
            first.ObserveAsync(new PipelineStepStarted(runId, "third"), default).AsTask(),
            second.ObserveAsync(new PipelineStepStarted(runId, "fourth"), default).AsTask()
        );

        var entries = await secondStore.ReadJournalAsync(runId);
        entries.Select(entry => entry.Sequence).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task ObserverFactory_RejectsCompositionConflictsAndTerminalRuns()
    {
        var path = DatabasePath();
        var store = new SqliteLedgerStore(path);
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
        var secondAppend = async () =>
            await new SqliteLedgerStore(path).AppendAsync(
                runId,
                new RuntimeJournalRecord(RuntimeJournalKind.StepStarted, "late"),
                default
            );

        await reopen.Should().ThrowAsync<LedgerConflictException>();
        await append.Should().ThrowAsync<LedgerConflictException>();
        await secondAppend.Should().ThrowAsync<LedgerConflictException>();
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

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public async Task OtherSchemaVersion_FailsWithoutRewritingTheDatabase(int version)
    {
        var path = DatabasePath();
        await ExecuteSqlAsync(path, $"PRAGMA user_version = {version};");
        var store = new SqliteLedgerStore(path);

        var initialize = async () => await store.InitializeAsync();

        await initialize
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*'{version}'*expected '2'*");
    }

    [Fact]
    public async Task RelationalConstraintsRejectInvalidRows()
    {
        var path = DatabasePath();
        var store = await CreateStoreAsync(path);
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "test");
        var id = runId.ToString("N");
        await ExecuteSqlAsync(
            path,
            $"INSERT INTO journal (run_id, sequence, record, recorded_at) VALUES ('{id}', 1, '{{}}', 0);"
        );
        var invalidStatements = new[]
        {
            $"INSERT INTO runs VALUES ('{Guid.NewGuid():N}', '', 'Running', 0, 0, NULL);",
            $"INSERT INTO runs VALUES ('{Guid.NewGuid():N}', 'test', 'Unknown', 0, 0, NULL);",
            "INSERT INTO journal (run_id, sequence, record, recorded_at) VALUES ('missing', 1, '{}', 0);",
            $"INSERT INTO journal (run_id, sequence, record, recorded_at) VALUES ('{id}', 0, '{{}}', 0);",
            $"INSERT INTO journal (run_id, sequence, record, recorded_at) VALUES ('{id}', 1, '{{}}', 0);",
            $"INSERT INTO journal (run_id, sequence, record, recorded_at) VALUES ('{id}', 2, 'not json', 0);",
        };

        foreach (var sql in invalidStatements)
        {
            var execute = async () => await ExecuteSqlAsync(path, sql);
            await execute.Should().ThrowAsync<SqliteException>();
        }
    }

    public void Dispose() => _directory.Dispose();

    private string DatabasePath() => _directory.Combine("ledger.sqlite3");

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

    private static Task<LocalProcessResult> RunWorkerAsync(
        string databasePath,
        Guid runId,
        string stepId
    ) =>
        LocalProcess.RunAsync(
            new(
                "dotnet",
                [
                    Path.Combine(AppContext.BaseDirectory, "Tandem.Ledger.TestWorker.dll"),
                    databasePath,
                    runId.ToString("D"),
                    stepId,
                ]
            )
        );

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

    private sealed record ProbeDecision(string Rationale, IReadOnlyList<string> Evidence);

    private sealed record ProbeRequest(
        string Question,
        string ProposedApproach,
        IReadOnlyList<string> Evidence
    );

    /// <summary>Cancels the run once its step has started, then breaks the ledger file.</summary>
    private sealed class BreakLedgerAfterCancellationObserver(
        string databasePath,
        CancellationTokenSource runCancellation
    ) : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            if (observation is PipelineStepStarted)
            {
                runCancellation.Cancel();
            }
            if (observation is PipelineStepCancelled)
            {
                foreach (var suffix in new[] { "", "-shm", "-wal" })
                {
                    File.Delete(databasePath + suffix);
                }
                Directory.CreateDirectory(databasePath);
            }
            return ValueTask.CompletedTask;
        }
    }
}
