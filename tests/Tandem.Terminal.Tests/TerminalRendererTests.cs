using FluentAssertions;
using Spectre.Console.Testing;

namespace Tandem.Terminal.Tests;

public sealed class TerminalRendererTests
{
    [Fact]
    public void WideTerminalRendersWorkAndPipelineAsSideBySidePanes()
    {
        var console = new TestConsole().Width(140).Height(30);
        var renderer = new TerminalRenderer(console);

        renderer.Render(Model(("executor", "implementation"), ("reviewer", "accepted")));

        console.Output.Should().Contain("[reviewer]").And.Contain("Pipeline");
    }

    [Fact]
    public void WidePipelinePaneUsesItsContentWidthInsteadOfAQuarterOfTheTerminal()
    {
        var console = new TestConsole().Width(180).Height(24);
        TerminalPipelineEntry[] pipelineEntries =
        [
            new(
                "executor",
                StandardOutcomeKinds.Success,
                "Succeeded",
                TimeSpan.FromSeconds(1067.1),
                TerminalPipelineEntryStyle.Success
            ),
        ];

        new TerminalRenderer(console, pipelineLabels: ["executor"])
            .Render(Model(("executor", "done")), pipelineEntries);

        var pipelineBorder = console
            .Output.Split('\n')
            .Single(line => line.Contains("Pipeline", StringComparison.Ordinal));
        var paneStart = pipelineBorder.LastIndexOf('╭');
        var paneEnd = pipelineBorder.LastIndexOf('╮');
        paneStart.Should().BeGreaterThan(0);
        (paneEnd - paneStart + 1).Should().BeLessThan(40);
    }

    [Fact]
    public void PipelinePaneWidthDoesNotChangeWhenALongerRuntimeLabelAppears()
    {
        var initialConsole = new TestConsole().Width(180).Height(24);
        var laterConsole = new TestConsole().Width(180).Height(24);
        var initial = Model(("Foo", "working"));
        var later = Model(("Foo", "working"), ("FooBarBaz", "working"));

        new TerminalRenderer(initialConsole, pipelineLabels: ["Foo", "FooBarBaz"])
            .Render(initial);
        new TerminalRenderer(laterConsole, pipelineLabels: ["Foo", "FooBarBaz"])
            .Render(later);

        PipelinePaneWidth(initialConsole.Output).Should().Be(PipelinePaneWidth(laterConsole.Output));
    }

    [Fact]
    public void RunHeaderContainsOnlyRunIdStatusAndElapsedTime()
    {
        var console = new TestConsole().Width(140).Height(24);

        new TerminalRenderer(console).Render(Model(("implementer", "working")));

        console
            .Output.Should()
            .Contain($"{_runId:N}  Running  00:00:00")
            .And.NotContain($"pipeline  {_runId:N}")
            .And.NotContain("Running  implementer")
            .And.NotContain("Running  model");
    }

    [Fact]
    public void WorkHeaderShowsModelWithoutStateOrParticipant()
    {
        var console = new TestConsole().Width(140).Height(24);

        new TerminalRenderer(console).Render(Model(("reviewer", "accepted")));

        console
            .Output.Should()
            .Contain("model")
            .And.NotContain("reviewer · model")
            .And.NotContain("model · running");
    }

    [Theory]
    [InlineData(800, "800")]
    [InlineData(1000, "1k")]
    [InlineData(131072, "131k")]
    public void WorkHeaderShowsCurrentContextAndWindowNextToModel(int tokens, string expected)
    {
        var console = new TestConsole().Width(140).Height(24);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TerminalSnapshot(
            "pipeline",
            _runId,
            TerminalPipelineStatus.Running,
            null,
            "executor",
            "deepseek",
            now,
            null,
            [new("executor", now)],
            [new("executor", TranscriptKind.Text, "working")],
            0,
            0,
            800,
            tokens,
            0,
            null,
            "",
            null,
            null
        );

        new TerminalRenderer(console).Render(snapshot);

        console.Output.Should().Contain($"deepseek · ctx 800/{expected}");
    }

    [Fact]
    public void NarrowTerminalRendersWorkAndPipelineAsStackedPanes()
    {
        var console = new TestConsole().Width(80).Height(24);
        var renderer = new TerminalRenderer(console);

        renderer.Render(Model(("executor", "implementation")));

        var output = console.Output;
        output
            .IndexOf("implementation", StringComparison.Ordinal)
            .Should()
            .BeLessThan(output.IndexOf("Pipeline", StringComparison.Ordinal));
    }

    [Fact]
    public void CompleteJsonPrettyPrintsAfterStreamingCompletes()
    {
        var output = Render(
            ("planner", "{\"decision\":\"Proceed\",\"approved\":true,\"constraints\":[]}")
        );

        output.Should().Contain("\"decision\": \"Proceed\"").And.Contain("\"approved\": true");
        output
            .Split('\n')
            .Count(line => line.Contains("decision") || line.Contains("approved"))
            .Should()
            .Be(2);
    }

    [Fact]
    public void IncompleteJsonRemainsPlainText()
    {
        Render(("planner", "{\"decision\":\"Proceed\",\"constraints\":"))
            .Should()
            .Contain("{\"decision\":\"Proceed\",\"constraints\":");
    }

    [Fact]
    public void CompletedJsonFencePrettyPrintsWithoutFence()
    {
        var output = Render(("reviewer", "```json\n{\"decision\":\"Accept\"}\n```"));

        output.Should().Contain("\"decision\": \"Accept\"").And.NotContain("```json");
    }

    [Fact]
    public void PrefixedJsonPrettyPrintsAndPreservesUnicode()
    {
        var output = Render(
            ("executor", "planner request:\n{\"approach\":\"Call `complete` → return todo\"}")
        );

        output
            .Should()
            .Contain("planner request:")
            .And.Contain("\"approach\": \"Call `complete` → return todo\"")
            .And.NotContain("\\u0060");
    }

    [Fact]
    public void CoalescedCorrectionPrettyPrintsAdjacentJsonDocuments()
    {
        var output = Render(("planner", "{\"decision\":\"Constrain\"} {\"decision\":\"Proceed\"}"));

        output.Split('\n').Count(line => line.Contains("\"decision\"")).Should().Be(2);
    }

    [Fact]
    public void TranscriptShowsAllStepsInEntryOrder()
    {
        var output = Render(
            ("executor", "first turn"),
            ("planner", "planner response"),
            ("executor", "second turn")
        );

        output.Should().Contain("[executor]").And.Contain("[planner] ");
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
    public void StepTagsUseOneRightPaddedGutterWidth()
    {
        var output = Render(("executor", "implementation"), ("verify", "verification"));

        output.Should().Contain("[executor] ").And.Contain("[verify]   ");
        var implementation = output.Split('\n').Single(line => line.Contains("implementation"));
        var verification = output.Split('\n').Single(line => line.Contains("verification"));
        implementation
            .IndexOf("implementation", StringComparison.Ordinal)
            .Should()
            .Be(verification.IndexOf("verification", StringComparison.Ordinal));
    }

    [Fact]
    public void MultilineMessageLabelsOnceAndPreservesIndentation()
    {
        var output = Render(("executor", "command:\n{\n  \"verification\": \"passed\"\n}"));

        output.Split("[executor]", StringSplitOptions.None).Should().HaveCount(2);
        var brace = output.Split('\n').Single(line => line.Contains('{'));
        var verification = output.Split('\n').Single(line => line.Contains("\"verification\""));
        verification.IndexOf('"').Should().Be(brace.IndexOf('{') + 2);
    }

    [Fact]
    public void ScrollHomeShowsOldestTranscriptAndFollowHint()
    {
        var console = new TestConsole().Width(120).Height(16);
        var renderer = new TerminalRenderer(console);
        var model = Model(
            Enumerable.Range(0, 40).Select(index => ("executor", $"line-{index:D2}\n")).ToArray()
        );

        renderer.Render(model);
        console.Output.Should().Contain("line-39");
        renderer.ScrollHome();
        renderer.Render(model);

        console.Output.Should().Contain("line-00").And.Contain("End follow");
    }

    [Fact]
    public void PipelinePaneAlignsLabelsDurationsAndTruncatesLongResults()
    {
        var console = new TestConsole().Width(180).Height(24);
        var renderer = new TerminalRenderer(console, pipelineLabels: ["prepare", "verification"]);
        TerminalPipelineEntry[] pipelineEntries =
        [
            new(
                "prepare",
                "tandem.success",
                "Succeeded",
                TimeSpan.FromMilliseconds(778),
                TerminalPipelineEntryStyle.Success
            ),
            new(
                "verification",
                "passed",
                "Tests passed",
                TimeSpan.FromSeconds(1),
                TerminalPipelineEntryStyle.Success
            ),
        ];
        var model = Model(("executor", "done")) with
        {
            CurrentContextTokens = 8_700,
            ContextWindowTokens = 32_000,
        };

        renderer.Render(model, pipelineEntries);

        console
            .Output.Should()
            .Contain("prepare")
            .And.Contain("778ms")
            .And.Contain("Succeeded")
            .And.Contain("verification")
            .And.Contain("1.0s")
            .And.Contain("Tests pa…")
            .And.NotContain("tandem.success");
    }

    [Fact]
    public void PipelinePaneDoesNotWrapLongDurationsOntoAnotherLine()
    {
        var console = new TestConsole().Width(140).Height(24);
        TerminalPipelineEntry[] pipelineEntries =
        [
            new(
                "executor",
                StandardOutcomeKinds.Success,
                "Succeeded",
                TimeSpan.FromSeconds(1067.1),
                TerminalPipelineEntryStyle.Success
            ),
        ];

        new TerminalRenderer(console).Render(Model(("executor", "done")), pipelineEntries);

        console.Output.Should().Contain("1067.1s");
        console.Output.Split('\n').Should().NotContain(line => line.Trim() == "s");
    }

    [Fact]
    public void NarrowPipelinePaneTruncatesLongLabelsWithoutWrappingCharacters()
    {
        var console = new TestConsole().Width(40).Height(24);
        TerminalPipelineEntry[] pipelineEntries =
        [
            new(
                "extraordinarily-long-step-name",
                "running",
                "Running",
                Style: TerminalPipelineEntryStyle.Information
            ),
        ];

        new TerminalRenderer(console).Render(Model(("executor", "working")), pipelineEntries);

        console.Output.Should().Contain("extraordinarily-l…").And.Contain("Running");
        console.Output.Split('\n').Count(line => line.Trim() is "e" or "p").Should().Be(0);
    }

    [Fact]
    public void StackedPipelinePaneUsesAvailableWidthForLabels()
    {
        var console = new TestConsole().Width(80).Height(24);
        TerminalPipelineEntry[] pipelineEntries =
        [
            new(
                "verification",
                "passed",
                "Tests passed",
                TimeSpan.FromSeconds(1),
                TerminalPipelineEntryStyle.Success
            ),
        ];

        new TerminalRenderer(console).Render(Model(("executor", "working")), pipelineEntries);

        console
            .Output.Should()
            .Contain("verification")
            .And.Contain("1.0s")
            .And.Contain("Tests passed");
    }

    [Fact]
    public async Task InteractiveTranscriptIncludesToolAndCommandActivity()
    {
        var model = new TerminalModel("pipeline", _runId, TimeProvider.System, 100, 10_000, null, null);
        model.Apply(new PipelineAgentUpdated(_runId, "agent", new AgentUpdate.Text("visible")));
        model.Apply(
            new PipelineAgentUpdated(
                _runId,
                "agent",
                new AgentUpdate.ToolStarted("call", "search", default)
            )
        );
        model.Apply(new PipelineCommandOutput(_runId, "verify", "test", "hidden", 0));

        var snapshot = model.Snapshot();

        snapshot
            .Transcript.Select(entry => entry.Kind)
            .Should()
            .Equal(TranscriptKind.Text, TranscriptKind.ToolStarted, TranscriptKind.Command);
        await Task.CompletedTask;
    }

    [Fact]
    public void CompletedWorkTitleShowsModelWithoutStateOrParticipant()
    {
        var now = DateTimeOffset.UtcNow;
        var model = Model(("reviewer", "accepted")) with
        {
            Status = TerminalPipelineStatus.Succeeded,
            ActiveStep = null,
            Visits =
            [
                new StepVisit("reviewer", now, now, StandardOutcomeKinds.Success),
                new StepVisit("done", now, now, StandardOutcomeKinds.Success),
            ],
        };

        var console = new TestConsole().Width(120).Height(24);

        new TerminalRenderer(console).Render(model);

        console
            .Output.Should()
            .Contain("model")
            .And.NotContain("reviewer · model")
            .And.NotContain("model · done");
    }

    private static readonly Guid _runId = Guid.CreateVersion7();

    private static int PipelinePaneWidth(string output)
    {
        var border = output
            .Split('\n')
            .Single(line => line.Contains("Pipeline", StringComparison.Ordinal));
        return border.LastIndexOf('╮') - border.LastIndexOf('╭') + 1;
    }

    private static string Render(params (string StepId, string Text)[] entries)
    {
        var console = new TestConsole().Width(120).Height(30);
        new TerminalRenderer(console).Render(Model(entries));
        return console.Output;
    }

    private static TerminalSnapshot Model(params (string StepId, string Text)[] entries)
    {
        var now = DateTimeOffset.UtcNow;
        var transcript = entries
            .Select(entry => new TranscriptEntry(entry.StepId, TranscriptKind.Text, entry.Text))
            .ToArray();
        var visits = entries
            .Select(entry => entry.StepId)
            .Distinct()
            .Select(step => new StepVisit(step, now))
            .ToArray();
        return new TerminalSnapshot(
            "pipeline",
            _runId,
            TerminalPipelineStatus.Running,
            null,
            entries.LastOrDefault().StepId,
            "model",
            now,
            null,
            visits,
            transcript,
            0,
            0,
            0,
            null,
            0,
            null,
            "",
            null,
            null
        );
    }
}
