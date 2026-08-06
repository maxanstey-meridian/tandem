using System.Text.Json;
using FluentAssertions;
using Spectre.Console.Testing;
using Tandem.Domain;
using Tandem.Infrastructure.Dashboard;
using Tandem.Infrastructure.Projection;

namespace Tandem.Tests.Infrastructure;

internal static class DashboardTestEvents
{
    private static int _seq;

    internal static RunEvent Make(
        string blockId,
        string kind,
        string message,
        JsonElement? data = null,
        DateTimeOffset? ts = null
    ) =>
        new(
            $"evt-{++_seq}",
            ts ?? DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            blockId,
            kind,
            message,
            data
        );

    internal static List<RunEvent> RepresentativeSequence()
    {
        var runId = Guid.CreateVersion7();
        var t0 = DateTimeOffset.UtcNow;
        var n = 0;

        RunEvent E(string block, string kind, string msg, JsonElement? data = null) =>
            new($"e{++n}", t0.AddSeconds(n), runId, block, kind, msg, data);

        return
        [
            E("", "run.started", $"Run {runId:N} started from /packet.md"),
            E("prepare", "block.started", "started"),
            E(
                "prepare",
                "block.completed",
                "done",
                JsonSerializer.SerializeToElement(
                    new { kind = "workspace_prepared", summary = "ok" }
                )
            ),
            E("executor", "block.started", "started"),
            E("executor", "agent.reasoning", "I should check the codebase…"),
            E(
                "executor",
                "tool.started",
                "read_file",
                JsonSerializer.SerializeToElement(new { callId = "c1", name = "read_file" })
            ),
            E(
                "executor",
                "tool.completed",
                "done",
                JsonSerializer.SerializeToElement(new { callId = "c1", success = true })
            ),
            E("executor", "agent.text", "Editing the file now."),
            E(
                "executor",
                "block.completed",
                "done",
                JsonSerializer.SerializeToElement(
                    new { kind = "executor_complete", summary = "done" }
                )
            ),
            E(
                "planner",
                "block.completed",
                "proceed",
                JsonSerializer.SerializeToElement(new { kind = "planner_proceed", summary = "go" })
            ),
            E("verify", "block.started", "started"),
            E(
                "verify",
                "command.output",
                "[PASS] npm test",
                JsonSerializer.SerializeToElement(new { command = "npm test", exitCode = 0 })
            ),
            E(
                "verify",
                "block.completed",
                "passed",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        kind = "verification_passed",
                        summary = "ok",
                        exitCode = 0,
                    }
                )
            ),
            E(
                "reviewer",
                "block.completed",
                "accepted",
                JsonSerializer.SerializeToElement(
                    new { kind = "review_accepted", summary = "good" }
                )
            ),
            E("", "run.ready", "Run ready, candidate: abc123def456789"),
        ];
    }
}

public sealed class DashboardReducerTests
{
    private static RunEvent Evt(
        string blockId,
        string kind,
        string message,
        JsonElement? data = null,
        DateTimeOffset? ts = null
    ) =>
        new(
            $"{blockId}-{kind}-{Guid.NewGuid():N}",
            ts ?? DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            blockId,
            kind,
            message,
            data
        );

    [Fact]
    public void Apply_RunStarted_SetsRunIdAndStatus()
    {
        var model = DashboardReducer.FromEvents([
            Evt("", "run.started", "Run abc123 started from /packet.md"),
        ]);

        model.RunId.Should().Be("abc123");
        model.PacketPath.Should().Be("/packet.md");
        model.Status.Should().Be(RunStatus.Running);
        model.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public void Apply_BlockStarted_SetsActiveBlock()
    {
        var ts = DateTimeOffset.UtcNow;
        var model = DashboardReducer.FromEvents([
            Evt("executor", "block.started", "started", ts: ts),
        ]);

        model.ActiveBlockId.Should().Be("executor");
        model.Blocks.Should().Contain(b => b.BlockId == "executor" && b.IsActive);
    }

    [Fact]
    public void Apply_BlockCompleted_AddsPipelineHistoryAndCompletesBlock()
    {
        var model = DashboardReducer.FromEvents(
            new[]
            {
                Evt(
                    "prepare",
                    "block.started",
                    "started",
                    ts: DateTimeOffset.UtcNow.AddSeconds(-2)
                ),
                Evt(
                    "prepare",
                    "block.completed",
                    "done",
                    JsonSerializer.SerializeToElement(
                        new { kind = "workspace_prepared", summary = "ok" }
                    ),
                    ts: DateTimeOffset.UtcNow
                ),
            }
        );

        var block = model.Blocks.Single(b => b.BlockId == "prepare");
        block.IsCompleted.Should().BeTrue();
        block.OutcomeKind.Should().Be("workspace_prepared");
        block.OutcomeSummary.Should().Be("ok");
        model.PipelineHistory.Should().ContainSingle();
        model.PipelineHistory[0].BlockId.Should().Be("prepare");
        model.PipelineHistory[0].Kind.Should().Be("workspace_prepared");
    }

    [Fact]
    public void Apply_AgentReasoningAndText_AppendsTranscriptLines()
    {
        var model = DashboardReducer.FromEvents(
            new[]
            {
                Evt("executor", "block.started", "started"),
                Evt("executor", "agent.reasoning", "thinking hard"),
                Evt("executor", "agent.text", "doing the thing"),
            }
        );

        var block = model.Blocks.Single(b => b.BlockId == "executor");
        block.Lines.Should().HaveCount(2);
        block.Lines[0].Kind.Should().Be(EventKinds.AgentReasoning);
        block.Lines[1].Kind.Should().Be(EventKinds.AgentText);
    }

    [Fact]
    public void Apply_AdjacentStreamingChunks_CoalescesWithoutInventingLineBreaks()
    {
        var model = DashboardReducer.FromEvents(
            new[]
            {
                Evt("planner", "block.started", "started"),
                Evt("planner", "agent.text", "The proposed a"),
                Evt("planner", "agent.text", "pproach implements"),
                Evt("planner", "agent.text", "\n\n"),
                Evt("planner", "agent.text", "both outcomes."),
            }
        );

        var block = model.Blocks.Single(b => b.BlockId == "planner");
        block.Lines.Should().ContainSingle();
        block.Lines[0].Text.Should().Be("The proposed approach implements\n\nboth outcomes.");
        model.ActiveBlockId.Should().Be("planner");
    }

    [Fact]
    public void Apply_ActivityMovesBackToPriorBlock_DeactivatesStaleBlock()
    {
        var model = DashboardReducer.FromEvents(
            new[]
            {
                Evt("executor", EventKinds.AgentText, "proposing"),
                Evt("planner", EventKinds.AgentText, "{\"decision\":\"Proceed\"}"),
                Evt("executor", EventKinds.AgentText, "implementing"),
            }
        );

        model.ActiveBlockId.Should().Be("executor");
        model.Blocks.Single(block => block.BlockId == "executor").IsActive.Should().BeTrue();
        model.Blocks.Single(block => block.BlockId == "planner").IsActive.Should().BeFalse();
    }

    [Fact]
    public void Apply_ToolStartedAndCompleted_RecordsTool()
    {
        var model = DashboardReducer.FromEvents(
            new[]
            {
                Evt("executor", "block.started", "started"),
                Evt(
                    "executor",
                    "tool.started",
                    "read_file",
                    JsonSerializer.SerializeToElement(new { callId = "c1", name = "read_file" })
                ),
                Evt(
                    "executor",
                    "tool.completed",
                    "done",
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            callId = "c1",
                            success = true,
                            name = "read_file",
                        }
                    )
                ),
            }
        );

        var block = model.Blocks.Single(b => b.BlockId == "executor");
        block.Lines.Should().HaveCount(2);
        block
            .Lines[0]
            .Should()
            .BeEquivalentTo(new { Kind = EventKinds.ToolStarted, ToolName = "read_file" });
        block
            .Lines[1]
            .Should()
            .BeEquivalentTo(
                new
                {
                    Kind = EventKinds.ToolCompleted,
                    ToolName = "read_file",
                    ToolSuccess = true,
                }
            );
    }

    [Fact]
    public void Apply_CommandOutput_AppendsTranscript()
    {
        var model = DashboardReducer.FromEvents(
            new[]
            {
                Evt("verify", "block.started", "started"),
                Evt(
                    "verify",
                    "command.output",
                    "[PASS] npm test\nAll good",
                    JsonSerializer.SerializeToElement(new { command = "npm test", exitCode = 0 })
                ),
            }
        );

        var block = model.Blocks.Single(b => b.BlockId == "verify");
        block.Lines.Should().ContainSingle().Which.Kind.Should().Be(EventKinds.CommandOutput);
    }

    [Fact]
    public void Apply_AgentUsage_UpdatesContextGaugeAndModel()
    {
        var data = JsonSerializer.SerializeToElement(
            new
            {
                inputTokens = 62000,
                outputTokens = 3100,
                reasoningTokens = 1800,
                model = "deepseek/deepseek-v4-flash-0731",
                contextWindowTokens = 200000,
            }
        );

        var model = DashboardReducer.FromEvents([
            Evt("executor", EventKinds.AgentUsage, "usage", data),
        ]);

        model.CurrentContextTokens.Should().Be(65100);
        model.ContextWindowTokens.Should().Be(200000);
        model.Model.Should().Be("deepseek/deepseek-v4-flash-0731");
    }

    [Fact]
    public void Apply_RunReady_SetsStatusAndCandidate()
    {
        var model = DashboardReducer.FromEvents([
            Evt("", "run.ready", "Run ready, candidate: abc123def456"),
        ]);

        model.Status.Should().Be(RunStatus.Ready);
        model.CandidateSha.Should().Be("abc123def456");
        model.CompletedAt.Should().NotBeNull();
        model.Blocks.Should().NotContain(block => block.IsActive);
        model.IsTerminal.Should().BeTrue();
        model.IsReady.Should().BeTrue();
    }

    [Fact]
    public void Apply_RunReadyWithNoneCandidate_KeepsNull()
    {
        var model = DashboardReducer.FromEvents([
            Evt("", "run.ready", "Run ready, candidate: (none)"),
        ]);

        model.Status.Should().Be(RunStatus.Ready);
        model.CandidateSha.Should().BeNull();
    }

    [Fact]
    public void Apply_RunFailed_SetsTerminal()
    {
        var model = DashboardReducer.FromEvents([Evt("", "run.failed", "boom")]);

        model.Status.Should().Be(RunStatus.Failed);
        model.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Apply_HumanRequested_SetsPendingRequestAndWaiting()
    {
        var data = JsonSerializer.SerializeToElement(
            new
            {
                sourceBlockId = "planner",
                question = "Which pattern?",
                reason = "ambiguous",
            }
        );
        var model = DashboardReducer.FromEvents([
            Evt("human-question", "human.requested", "Q", data),
        ]);

        model.PendingHumanRequest.Should().NotBeNull();
        model.PendingHumanRequest!.SourceBlockId.Should().Be("planner");
        model.PendingHumanRequest.Question.Should().Be("Which pattern?");
        model.PendingHumanRequest.Reason.Should().Be("ambiguous");
        model.Status.Should().Be(RunStatus.WaitingForHuman);
    }

    [Fact]
    public void Apply_HumanAnswered_ClearsRequest()
    {
        var data = JsonSerializer.SerializeToElement(
            new
            {
                sourceBlockId = "planner",
                question = "Q?",
                reason = "",
            }
        );
        var model = DashboardReducer.FromEvents(
            new[]
            {
                Evt("human-question", "human.requested", "Q", data),
                Evt("apply-human-answer", "human.answered", "answered"),
            }
        );

        model.PendingHumanRequest.Should().BeNull();
        model.Status.Should().Be(RunStatus.Running);
    }

    [Fact]
    public void Apply_RunPublished_SetsPublishedBranch()
    {
        var model = DashboardReducer.FromEvents([
            Evt("", "run.published", "Published: tandem/feat-abc12345\nCommit: def789"),
        ]);

        model.PublishedBranch.Should().Be("tandem/feat-abc12345");
    }

    [Fact]
    public async Task EventFeed_DeduplicatesDuplicateEventIds()
    {
        var events = DashboardTestEvents.RepresentativeSequence();
        var dir = Path.Combine(Path.GetTempPath(), "tandem-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var eventStore = new EventStore(dir);
            await eventStore.AppendRangeAsync(events);
            await eventStore.AppendRangeAsync(events);

            var feed = new DashboardEventFeed(dir);
            var loaded = await feed.ReadExistingAsync();

            loaded.Should().HaveCount(events.Count);
            loaded.Select(e => e.EventId).Should().OnlyHaveUniqueItems();
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
    public async Task EventFeed_WaitsForCompleteLineAndSkipsMalformedHistory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tandem-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "events.jsonl");
            var evt = DashboardTestEvents.Make("planner", EventKinds.AgentText, "hello");
            var json = JsonSerializer.Serialize(
                evt,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            );
            await File.WriteAllTextAsync(path, "not-json\n" + json[..^4]);

            var feed = new DashboardEventFeed(dir);
            (await feed.ReadExistingAsync()).Should().BeEmpty();

            await File.AppendAllTextAsync(path, json[^4..] + "\n");
            var loaded = await feed.PollNewAsync();

            loaded.Should().ContainSingle();
            loaded[0].Message.Should().Be("hello");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class DashboardReplayTests
{
    [Fact]
    public void RepresentativeSequence_ProducesExpectedModel()
    {
        var events = DashboardTestEvents.RepresentativeSequence();
        var model = DashboardReducer.FromEvents(events);

        model.RunId.Should().NotBeEmpty();
        model.Status.Should().Be(RunStatus.Ready);
        model.CandidateSha.Should().Be("abc123def456789");

        model.Blocks.Should().Contain(b => b.BlockId == "executor" && b.IsCompleted);
        var executor = model.Blocks.Single(b => b.BlockId == "executor");
        executor.Lines.Should().HaveCount(4);
        executor.Lines.Should().Contain(l => l.Kind == EventKinds.AgentReasoning);
        executor.Lines.Should().Contain(l => l.Kind == EventKinds.AgentText);
        executor
            .Lines.Should()
            .Contain(l => l.Kind == EventKinds.ToolStarted && l.ToolName == "read_file");
        executor
            .Lines.Should()
            .Contain(l => l.Kind == EventKinds.ToolCompleted && l.ToolSuccess == true);

        model.PipelineHistory.Should().NotBeEmpty();
        model.PipelineHistory.Should().Contain(p => p.BlockId == "verify" && p.ExitCode == 0);
        model.PipelineHistory.Should().Contain(p => p.BlockId == "reviewer");
    }

    [Fact]
    public void Replay_IntoNewModel_ProducesIdenticalState()
    {
        var events = DashboardTestEvents.RepresentativeSequence();

        var first = DashboardReducer.FromEvents(events);
        var second = DashboardReducer.FromEvents([.. events]);

        second.Should().BeEquivalentTo(first, opts => opts.RespectingRuntimeTypes());
    }

    [Fact]
    public async Task FromEvents_PersistsJsonThenRebuilds_SameModel()
    {
        var events = DashboardTestEvents.RepresentativeSequence();
        var dir = Path.Combine(Path.GetTempPath(), "tandem-dash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var eventStore = new EventStore(dir);
            foreach (var evt in events)
            {
                await eventStore.AppendAsync(evt);
            }

            var load = await eventStore.ReadAllAsync();
            var model = DashboardReducer.FromEvents(load);

            model.Status.Should().Be(RunStatus.Ready);
            model.CandidateSha.Should().Be("abc123def456789");
            model.PipelineHistory.Should().NotBeEmpty();
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

public sealed class DashboardRendererTests
{
    private static DashboardModel ReadyModel() =>
        DashboardReducer.FromEvents(
            new[]
            {
                DashboardTestEvents.Make("", "run.started", "Run abc started from /p.md"),
                DashboardTestEvents.Make("executor", "block.started", "started"),
                DashboardTestEvents.Make("executor", "agent.text", "hello"),
                DashboardTestEvents.Make("", "run.ready", "Run ready, candidate: abc123"),
            }
        );

    [Fact]
    public void Render_NarrowTerminal_DoesNotThrow()
    {
        var console = new TestConsole().Width(60).Height(24);
        var renderer = new DashboardRenderer(console);
        var model = ReadyModel();

        var act = () => renderer.Render(model);

        act.Should().NotThrow();
        console.Output.Split('\n').Should().HaveCountLessThanOrEqualTo(25);
    }

    [Fact]
    public void Render_WideTerminal_DoesNotThrow()
    {
        var console = new TestConsole().Width(140).Height(40);
        var renderer = new DashboardRenderer(console);
        var model = ReadyModel();

        var act = () => renderer.Render(model);

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
        var data = JsonSerializer.SerializeToElement(
            new
            {
                sourceBlockId = "planner",
                question = "Which?",
                reason = "ambiguous",
            }
        );
        var model = DashboardReducer.FromEvents(
            new[]
            {
                new RunEvent(
                    "e1",
                    DateTimeOffset.UtcNow,
                    Guid.CreateVersion7(),
                    "human-question",
                    "human.requested",
                    "Q",
                    data
                ),
            }
        );

        var act = () => renderer.Render(model);

        act.Should().NotThrow();
        console.Output.Should().Contain("Which?");
    }

    [Fact]
    public void Render_SuccessfulToolCompletion_DoesNotShowDoneRow()
    {
        var console = new TestConsole().Width(100).Height(30);
        var renderer = new DashboardRenderer(console);
        var completed = JsonSerializer.SerializeToElement(
            new
            {
                callId = "c1",
                success = true,
                name = "read_file",
            }
        );
        var model = DashboardReducer.FromEvents(
            new[]
            {
                DashboardTestEvents.Make("executor", EventKinds.ToolStarted, "read_file"),
                DashboardTestEvents.Make("executor", EventKinds.ToolCompleted, "done", completed),
            }
        );

        renderer.Render(model);

        console.Output.Should().Contain("read_file");
        console.Output.Should().NotContain("done");
    }

    [Fact]
    public void Render_MergedTranscript_ShowsAllBlocks()
    {
        var console = new TestConsole().Width(120).Height(40);
        var renderer = new DashboardRenderer(console);
        var model = DashboardReducer.FromEvents(
            new[]
            {
                DashboardTestEvents.Make("executor", EventKinds.AgentText, "investigating repo"),
                DashboardTestEvents.Make("planner", EventKinds.AgentText, "decision: proceed"),
                DashboardTestEvents.Make("executor", EventKinds.AgentText, "implementing now"),
            }
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
        var model = DashboardReducer.FromEvents([
            DashboardTestEvents.Make(
                "planner",
                EventKinds.AgentText,
                "{\"decision\":\"Proceed\",\"approved\":true,\"constraints\":[]}"
            ),
        ]);

        renderer.Render(model);

        var output = console.Output;
        output.Should().Contain("\"decision\": \"Proceed\"");
        output.Should().Contain("\"approved\": true");
        output
            .Split('\n')
            .Count(line => line.Contains("decision") || line.Contains("approved"))
            .Should()
            .Be(2);
    }

    [Fact]
    public void Render_IncompleteStreamedJson_RemainsPlainText()
    {
        var console = new TestConsole().Width(120).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = DashboardReducer.FromEvents([
            DashboardTestEvents.Make(
                "planner",
                EventKinds.AgentText,
                "{\"decision\":\"Proceed\",\"constraints\":"
            ),
        ]);

        renderer.Render(model);

        console.Output.Should().Contain("{\"decision\":\"Proceed\",\"constraints\":");
    }

    [Fact]
    public void Render_CompletedJsonFence_PrettyPrintsWithoutFence()
    {
        var console = new TestConsole().Width(120).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = DashboardReducer.FromEvents([
            DashboardTestEvents.Make(
                "reviewer",
                EventKinds.AgentText,
                "```json\n{\"decision\":\"Accept\"}\n```"
            ),
        ]);

        renderer.Render(model);

        console.Output.Should().Contain("\"decision\": \"Accept\"");
        console.Output.Should().NotContain("```json");
    }

    [Fact]
    public void Render_PrefixedToolJson_PrettyPrintsAndPreservesCharacters()
    {
        var console = new TestConsole().Width(140).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = DashboardReducer.FromEvents([
            DashboardTestEvents.Make(
                "executor",
                EventKinds.ToolStarted,
                "ask_planner:\n{\"proposedApproach\":\"Call `markComplete` → return todo\"}"
            ),
        ]);

        renderer.Render(model);

        console.Output.Should().Contain("ask_planner:");
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
        var model = DashboardReducer.FromEvents([
            DashboardTestEvents.Make(
                "executor",
                EventKinds.ToolStarted,
                "ask_planner:\n{\"proposedApproach\":\"First segment followed by a deliberately long second segment that must wrap inside the JSON content column.\"}"
            ),
        ]);

        renderer.Render(model);

        var continuation = console
            .Output.Split('\n')
            .Single(line => line.Contains("second segment"));
        continuation.IndexOf("second segment", StringComparison.Ordinal).Should().BeGreaterThan(10);
    }

    [Fact]
    public void Render_MergedTranscript_TagsLinesWithBlockId()
    {
        var console = new TestConsole().Width(120).Height(40);
        var renderer = new DashboardRenderer(console);
        var model = DashboardReducer.FromEvents(
            new[]
            {
                DashboardTestEvents.Make("executor", EventKinds.AgentText, "checking files"),
                DashboardTestEvents.Make("planner", EventKinds.AgentText, "approving approach"),
            }
        );

        renderer.Render(model);

        console.Output.Should().Contain("[executor]");
        console.Output.Should().Contain("[ planner]");
    }

    [Fact]
    public void Render_BlockTags_CenterShortNamesToOneGutterWidth()
    {
        var console = new TestConsole().Width(120).Height(30);
        var renderer = new DashboardRenderer(console);
        var model = DashboardReducer.FromEvents([
            DashboardTestEvents.Make("executor", EventKinds.AgentText, "implementation"),
            DashboardTestEvents.Make("verify", EventKinds.AgentText, "verification"),
        ]);

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
    public void Render_MergedTranscript_ActiveBlockLinesLast()
    {
        var console = new TestConsole().Width(120).Height(40);
        var renderer = new DashboardRenderer(console);
        var model = DashboardReducer.FromEvents(
            new[]
            {
                DashboardTestEvents.Make("executor", EventKinds.AgentText, "first turn"),
                DashboardTestEvents.Make("planner", EventKinds.AgentText, "planner response"),
                DashboardTestEvents.Make("executor", EventKinds.AgentText, "second turn"),
            }
        );

        renderer.Render(model);

        var output = console.Output;
        var plannerIdx = output.IndexOf("planner response", StringComparison.Ordinal);
        var secondTurnIdx = output.IndexOf("second turn", StringComparison.Ordinal);
        plannerIdx.Should().BeGreaterThan(0);
        secondTurnIdx
            .Should()
            .BeGreaterThan(plannerIdx, "active block lines appear after prior blocks");
    }

    [Fact]
    public void Render_ToolCalls_ShowUsefulArguments()
    {
        var console = new TestConsole().Width(140).Height(40);
        var renderer = new DashboardRenderer(console);
        var plannerData = JsonSerializer.SerializeToElement(
            new { callId = "c1", name = "ask_planner" }
        );
        var readData = JsonSerializer.SerializeToElement(
            new { callId = "c2", name = "file_access_read" }
        );
        var model = DashboardReducer.FromEvents(
            new[]
            {
                DashboardTestEvents.Make(
                    "executor",
                    EventKinds.ToolStarted,
                    """
                    ask_planner:
                    {
                      "question": "Should markComplete reuse TodoStore.add?"
                    }
                    """,
                    plannerData
                ),
                DashboardTestEvents.Make(
                    "executor",
                    EventKinds.ToolStarted,
                    "file_access_read: src/service.ts",
                    readData
                ),
            }
        );

        renderer.Render(model);

        console
            .Output.Should()
            .Contain("\"question\": \"Should markComplete reuse TodoStore.add?\"");
        console.Output.Should().Contain("file_access_read: src/service.ts");
        console.Output.Should().NotContain("file_access_read…");
    }

    [Fact]
    public void Render_MultilineMessage_LabelsOnceAndPreservesIndentation()
    {
        var console = new TestConsole().Width(140).Height(30);
        var renderer = new DashboardRenderer(console);
        var data = JsonSerializer.SerializeToElement(new { callId = "c1", name = "ask_planner" });
        var model = DashboardReducer.FromEvents([
            DashboardTestEvents.Make(
                "executor",
                EventKinds.ToolStarted,
                "ask_planner:\n{\n  \"question\": \"May I proceed?\"\n}",
                data
            ),
        ]);

        renderer.Render(model);

        var output = console.Output;
        output.Split("[executor]", StringSplitOptions.None).Should().HaveCount(2);
        var braceLine = output.Split('\n').Single(line => line.Contains('{'));
        var questionLine = output.Split('\n').Single(line => line.Contains("\"question\""));
        questionLine.IndexOf('"').Should().Be(braceLine.IndexOf('{') + 2);
    }

    [Fact]
    public void Render_LongToolCall_KeepsNewestOutputVisible()
    {
        var console = new TestConsole().Width(100).Height(18);
        var renderer = new DashboardRenderer(console);
        var plannerData = JsonSerializer.SerializeToElement(
            new { callId = "c1", name = "ask_planner" }
        );
        var longCall =
            "ask_planner:\n{\n"
            + string.Join(
                ",\n",
                Enumerable.Range(1, 30).Select(i => $"  \"line{i}\": \"value{i}\"")
            )
            + "\n}";
        var model = DashboardReducer.FromEvents(
            new[]
            {
                DashboardTestEvents.Make("executor", EventKinds.ToolStarted, longCall, plannerData),
                DashboardTestEvents.Make("planner", EventKinds.AgentText, "LATEST PLANNER OUTPUT"),
            }
        );

        renderer.Render(model);

        console.Output.Should().Contain("LATEST PLANNER OUTPUT");
        console.Output.Split('\n').Should().HaveCountLessThanOrEqualTo(19);
    }
}
