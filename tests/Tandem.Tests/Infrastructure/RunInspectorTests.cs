using System.Text.Json;
using FluentAssertions;
using Tandem.Ledger;
using Tandem.Tool;

namespace Tandem.Tests.Infrastructure;

public sealed class RunInspectorTests : IDisposable
{
    [Fact]
    public async Task Inspect_AcceptedExcludesNonPersistentInteractionMetadata()
    {
        var (store, runId) = await CreateRunAsync();
        await new SqlitePipelineObserver(store.ForRun(runId)).ObserveAsync(
            new PipelineInteractionRequested<HumanQuestion>(
                runId,
                "human-review",
                "request-1",
                new HumanQuestion("Proceed?", "Approval required")
            ),
            CancellationToken.None
        );

        var inspection = await new RunInspector(store).InspectAsync(
            runId,
            true,
            null,
            null,
            CancellationToken.None
        );

        inspection.Items.Should().BeEmpty();
    }

    private readonly string _home = Path.Combine(
        Path.GetTempPath(),
        $"tandem-inspector-{Guid.NewGuid():N}"
    );

    [Fact]
    public async Task Inspect_PreservesLedgerSequenceWhenTimestampsMatch()
    {
        var (store, runId) = await CreateRunAsync();
        var ledger = store.ForRun(runId);
        await ledger.AppendAsync(
            PipelineJournal.Stream,
            "second-semantic-id",
            new RuntimeJournalRecord(RuntimeJournalKind.StepCompleted, "second")
        );
        await ledger.AppendAsync(
            PipelineJournal.Stream,
            "first-lexical-id",
            new RuntimeJournalRecord(RuntimeJournalKind.StepStarted, "first")
        );

        var inspection = await new RunInspector(store).InspectAsync(
            runId,
            false,
            null,
            null,
            CancellationToken.None
        );

        inspection.Items.Select(item => item.StepId).Should().Equal("second", "first");
        inspection.Items.Select(item => item.Sequence).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Inspect_AcceptedIncludesFailureEvidenceAndAppliesTypeFilter()
    {
        var (store, runId) = await CreateRunAsync();
        var observer = new SqlitePipelineObserver(store.ForRun(runId));
        await observer.ObserveAsync(
            new PipelineStepCompleted(
                runId,
                "verify",
                new PipelineRunOutcome(
                    StandardOutcomeKinds.Failed,
                    "verify",
                    "Failed",
                    JsonSerializer.SerializeToElement(
                        new FailureEvidence("failed", "Verification failed")
                    ),
                    TimeSpan.Zero
                ),
                PipelineAcceptedValue.FromPayload<FailureEvidence>(
                    JsonSerializer.SerializeToElement(
                        new FailureEvidence("failed", "Verification failed")
                    )
                )
            ),
            CancellationToken.None
        );

        var accepted = await new RunInspector(store).InspectAsync(
            runId,
            true,
            null,
            null,
            CancellationToken.None
        );
        var typed = await new RunInspector(store).InspectAsync(
            runId,
            false,
            null,
            "FailureEvidence",
            CancellationToken.None
        );

        accepted.Items.Should().ContainSingle().Which.Category.Should().Be("accepted");
        accepted.Items[0].Payload.Should().NotBeNull();
        typed.Items.Should().ContainSingle().Which.Category.Should().Be("accepted");
    }

    [Fact]
    public async Task Inspect_AcceptedIncludesSuccessfulStageState()
    {
        var (store, runId) = await CreateRunAsync();
        var payload = JsonSerializer.SerializeToElement(new RunnerState(3));
        await new SqlitePipelineObserver(store.ForRun(runId)).ObserveAsync(
            new PipelineStepCompleted(
                runId,
                "increment",
                new PipelineRunOutcome(
                    StandardOutcomeKinds.Success,
                    "increment",
                    "Succeeded",
                    default,
                    TimeSpan.Zero
                ),
                PipelineAcceptedValue.FromPayload<RunnerState>(payload)
            ),
            CancellationToken.None
        );

        var inspection = await new RunInspector(store).InspectAsync(
            runId,
            true,
            null,
            null,
            CancellationToken.None
        );

        var accepted = inspection.Items.Should().ContainSingle().Which;
        accepted.Category.Should().Be("accepted");
        accepted.ValueType.Should().Be(typeof(RunnerState).FullName);
        accepted.Payload!.Value.GetProperty("Count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Inspect_JsonDtoHasStableNamedContractAndPayload()
    {
        var (store, runId) = await CreateRunAsync();
        var observer = new SqlitePipelineObserver(store.ForRun(runId));
        await observer.ObserveAsync(
            new PipelineStructuredOutputAccepted(
                runId,
                "planner",
                "accepted-1",
                StandardOutcomeKinds.Success,
                "Example.Decision",
                JsonSerializer.SerializeToElement(new { decision = "proceed" })
            ),
            CancellationToken.None
        );
        var inspection = await new RunInspector(store).InspectAsync(
            runId,
            false,
            null,
            null,
            CancellationToken.None
        );

        var json = JsonSerializer.Serialize(
            inspection,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("runId").GetGuid().Should().Be(runId);
        root.GetProperty("contractVersion").GetInt32().Should().Be(1);
        root.GetProperty("composition").GetString().Should().Be("delivery");
        root.GetProperty("status").GetString().Should().Be("Running");
        var item = root.GetProperty("items")[0];
        item.GetProperty("category").GetString().Should().Be("accepted");
        item.GetProperty("identity").GetString().Should().Be("accepted-1");
        item.GetProperty("sequence").GetInt64().Should().Be(1);
        item.GetProperty("payload").GetProperty("decision").GetString().Should().Be("proceed");
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private async ValueTask<(SqliteLedgerStore Store, Guid RunId)> CreateRunAsync()
    {
        Directory.CreateDirectory(_home);
        var store = new SqliteLedgerStore(
            Path.Combine(_home, "ledger.sqlite3"),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero))
        );
        await store.InitializeAsync();
        var runId = Guid.CreateVersion7();
        await store.CreateRunAsync(runId, "delivery");
        return (store, runId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
