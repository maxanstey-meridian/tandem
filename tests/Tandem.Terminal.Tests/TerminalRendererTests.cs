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

        var line = console.Output.Split('\n').First(value => value.Contains("Pipeline"));
        line.Should().Contain("reviewer").And.Contain("Pipeline");
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
    public void WorkHeaderShowsParticipantThenModelThenState()
    {
        var console = new TestConsole().Width(140).Height(24);

        new TerminalRenderer(console).Render(Model(("reviewer", "accepted")));

        console
            .Output.Should()
            .Contain("reviewer · model · running")
            .And.NotContain("reviewer  · model");
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

        output.Should().Contain("[executor]").And.Contain("[ planner]");
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
    public void StepTagsUseOneCenteredGutterWidth()
    {
        var output = Render(("executor", "implementation"), ("verify", "verification"));

        output.Should().Contain("[executor]").And.Contain("[ verify ]");
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
    public void FooterShowsContextGaugeAndPipelinePaneShowsTypedEntries()
    {
        var console = new TestConsole().Width(140).Height(24);
        var renderer = new TerminalRenderer(console);
        TerminalPipelineEntry[] pipelineEntries =
        [
            new(
                "verify",
                "passed",
                "dotnet test",
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
            .Contain("8.7k/32k")
            .And.Contain("verify")
            .And.Contain("dotnet")
            .And.Contain("test");
    }

    [Fact]
    public async Task InteractiveTranscriptExcludesToolAndCommandActivity()
    {
        var model = new TerminalModel("pipeline", _runId, TimeProvider.System, 100, 10_000);
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

        snapshot.Transcript.Should().ContainSingle().Which.Text.Should().Be("visible");
        await Task.CompletedTask;
    }

    [Fact]
    public void CompletedWorkTitleUsesLastTranscriptParticipantInsteadOfCompletionNode()
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
            .Contain("reviewer · model · done")
            .And.NotContain("done · model · done");
    }

    private static readonly Guid _runId = Guid.CreateVersion7();

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
            ""
        );
    }
}
