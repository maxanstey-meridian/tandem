using System.Text.Json;
using FluentAssertions;
using Tandem.Domain;
using Tandem.Infrastructure.Projection;

namespace Tandem.Tests.Infrastructure;

public sealed class EventStoreTests
{
    [Fact]
    public async Task AppendAndRead_RoundTripsEvents()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new EventStore(dir);
            var runId = Guid.CreateVersion7();

            await store.AppendAsync(
                new RunEvent(
                    "evt-1",
                    DateTimeOffset.UtcNow,
                    runId,
                    "prepare",
                    "block.started",
                    "started",
                    null
                )
            );
            await store.AppendAsync(
                new RunEvent(
                    "evt-2",
                    DateTimeOffset.UtcNow,
                    runId,
                    "prepare",
                    "block.completed",
                    "done",
                    null
                )
            );
            await store.AppendAsync(
                new RunEvent(
                    "evt-3",
                    DateTimeOffset.UtcNow,
                    runId,
                    "executor",
                    "tool.started",
                    "file_access_read",
                    null
                )
            );

            var events = await store.ReadAllAsync();
            events.Should().HaveCount(3);
            events[0].EventId.Should().Be("evt-1");
            events[1].EventId.Should().Be("evt-2");
            events[2].EventId.Should().Be("evt-3");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadAll_CollapsesDuplicateEventIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new EventStore(dir);
            var runId = Guid.CreateVersion7();
            var ts = DateTimeOffset.UtcNow;

            await store.AppendAsync(
                new RunEvent("dup-1", ts, runId, "prepare", "block.started", "started", null)
            );
            await store.AppendAsync(
                new RunEvent("dup-1", ts, runId, "prepare", "block.started", "started", null)
            );
            await store.AppendAsync(
                new RunEvent("dup-2", ts, runId, "prepare", "block.completed", "done", null)
            );

            var events = await store.ReadAllAsync();
            events.Should().HaveCount(2);
            events[0].EventId.Should().Be("dup-1");
            events[1].EventId.Should().Be("dup-2");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadAll_EmptyDir_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new EventStore(dir);
            var events = await store.ReadAllAsync();
            events.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Append_FlushesImmediately_FileVisible()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new EventStore(dir);
            var runId = Guid.CreateVersion7();

            await store.AppendAsync(
                new RunEvent(
                    "evt-1",
                    DateTimeOffset.UtcNow,
                    runId,
                    "prepare",
                    "block.started",
                    "started",
                    null
                )
            );

            File.Exists(Path.Combine(dir, "events.jsonl")).Should().BeTrue();
            var lines = File.ReadLines(Path.Combine(dir, "events.jsonl")).ToList();
            lines.Should().HaveCount(1);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

public sealed class RunProjectionStoreTests
{
    [Fact]
    public async Task WriteAndRead_RoundTripsProjection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new RunProjectionStore(dir);
            var projection = RunProjection.Initial(
                Guid.CreateVersion7(),
                "durable-run-123",
                "/path/to/packet.md",
                "/path/to/repo",
                "/path/to/workspace"
            );

            await store.WriteAsync(projection);

            var read = store.Read();
            read.Should().NotBeNull();
            read!.Status.Should().Be(RunStatus.Running);
            read.DurableRunId.Should().Be("durable-run-123");
            read.PacketPath.Should().Be("/path/to/packet.md");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Write_OverwritesPreviousVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new RunProjectionStore(dir);
            var runId = Guid.CreateVersion7();

            await store.WriteAsync(RunProjection.Initial(runId, "dr-1", "p", "r", "w"));
            await store.WriteAsync(
                RunProjection.Initial(runId, "dr-1", "p", "r", "w") with
                {
                    Status = RunStatus.Ready,
                    CandidateSha = "abc123",
                }
            );

            var read = store.Read();
            read!.Status.Should().Be(RunStatus.Ready);
            read.CandidateSha.Should().Be("abc123");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Write_LeavesNoTempFileOnInterrupt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new RunProjectionStore(dir);

            await store.WriteAsync(
                RunProjection.Initial(Guid.CreateVersion7(), "dr-1", "p", "r", "w")
            );

            File.Exists(Path.Combine(dir, "run.json.tmp")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "run.json")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new RunProjectionStore(dir);
            store.Read().Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

public sealed class RunEventProjectorTests
{
    [Fact]
    public void DescribeToolCall_ShowsPlannerQuestionAndFilePath()
    {
        RunEventProjector
            .DescribeToolCall(
                "ask_planner",
                new Dictionary<string, object?>
                {
                    ["question"] = "Should markComplete reuse TodoStore.add?",
                    ["proposedApproach"] = "Use `markComplete` — preserve signatures.",
                }
            )
            .Should()
            .Be(
                """
                ask_planner:
                {
                  "question": "Should markComplete reuse TodoStore.add?",
                  "proposedApproach": "Use `markComplete` — preserve signatures."
                }
                """
            );

        RunEventProjector
            .DescribeToolCall(
                "file_access_read",
                new Dictionary<string, object?> { ["path"] = "src/service.ts" }
            )
            .Should()
            .Be("file_access_read: src/service.ts");
    }

    [Fact]
    public async Task EmitBlockStarted_WritesEventAndInvokesCallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var eventStore = new EventStore(dir);
            var runId = Guid.CreateVersion7();
            var received = new List<RunEvent>();

            var projector = new RunEventProjector(
                runId,
                "executor",
                eventStore,
                onEvent: evt => received.Add(evt)
            );

            await projector.EmitBlockStartedAsync();

            received.Should().HaveCount(1);
            received[0].Kind.Should().Be(EventKinds.BlockStarted);
            received[0].BlockId.Should().Be("executor");
            received[0].RunId.Should().Be(runId);

            var persisted = await eventStore.ReadAllAsync();
            persisted.Should().HaveCount(1);
            persisted[0].EventId.Should().Be(received[0].EventId);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EmitBlockCompleted_IncludesOutcomeData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var eventStore = new EventStore(dir);
            var runId = Guid.CreateVersion7();
            var projector = new RunEventProjector(runId, "prepare", eventStore);

            var outcome = new BlockOutcome(
                OutcomeKinds.WorkspacePrepared,
                "prepare",
                "Workspace prepared",
                JsonSerializer.SerializeToElement(new { sha = "abc" }),
                TimeSpan.FromSeconds(2)
            );

            await projector.EmitBlockCompletedAsync(outcome);

            var events = await eventStore.ReadAllAsync();
            events.Should().HaveCount(1);
            events[0].Kind.Should().Be(EventKinds.BlockCompleted);
            events[0].Data.Should().NotBeNull();
            var data = events[0].Data!.Value;
            data.GetProperty("kind").GetString().Should().Be(OutcomeKinds.WorkspacePrepared);
            data.GetProperty("summary").GetString().Should().Be("Workspace prepared");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EventIds_AreUniquePerBlock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var eventStore = new EventStore(dir);
            var runId = Guid.CreateVersion7();
            var projector = new RunEventProjector(runId, "executor", eventStore);

            await projector.EmitBlockStartedAsync();
            await projector.EmitBlockStartedAsync();
            await projector.EmitBlockStartedAsync();

            var events = await eventStore.ReadAllAsync();
            events.Should().HaveCount(3);
            events.Select(e => e.EventId).Should().OnlyHaveUniqueItems();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
