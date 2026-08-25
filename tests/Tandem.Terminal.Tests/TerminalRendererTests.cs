using System.Text.Json;
using FluentAssertions;
using Spectre.Console;
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

        new TerminalRenderer(console, pipelineLabels: ["executor"]).Render(
            Model(("executor", "done")),
            pipelineEntries
        );

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

        new TerminalRenderer(initialConsole, pipelineLabels: ["Foo", "FooBarBaz"]).Render(initial);
        new TerminalRenderer(laterConsole, pipelineLabels: ["Foo", "FooBarBaz"]).Render(later);

        PipelinePaneWidth(initialConsole.Output)
            .Should()
            .Be(PipelinePaneWidth(laterConsole.Output));
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

    [Theory]
    [InlineData(
        "file_access_read",
        "{\"path\":\"src/Cadence/DeliveryState.cs\"}",
        "file_access_read path=\"src/Cadence/DeliveryState.cs\""
    )]
    [InlineData(
        "file_access_grep",
        "{\"pattern\":\"MutationAuthorized\",\"path\":\"src/Cadence\",\"globPattern\":\"*.cs\"}",
        "file_access_grep globPattern=\"*.cs\" path=\"src/Cadence\" pattern=\"MutationAuthorized\""
    )]
    [InlineData("run_shell", "{\"command\":\"task check\"}", "run_shell command=\"task check\"")]
    [InlineData(
        "git_diff",
        "{\"staged\":false,\"path\":\"src/Cadence/DeliveryState.cs\"}",
        "git_diff path=\"src/Cadence/DeliveryState.cs\" staged=false"
    )]
    [InlineData(
        "custom",
        "{\"z\":[1,{\"nested\":true}],\"a\":null}",
        "custom a=null z=[1,{\"nested\":true}]"
    )]
    [InlineData("custom", "{}", "custom")]
    [InlineData("custom", "[1,{\"nested\":true}]", "custom arguments=[1,{\"nested\":true}]")]
    public void ToolStartsRenderGenericCompactArgumentsInOrdinalOrder(
        string toolName,
        string arguments,
        string expected
    )
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "tandem-work");

        var output = RenderTool(toolName, arguments, workingDirectory);

        output.Should().Contain("[executor] ↯");
        output.Should().Contain(expected).And.Contain(" in ").And.Contain(workingDirectory);
    }

    [Fact]
    public void ToolStartsUseDistinctColorsForToolArgumentsAndWorkingDirectory()
    {
        var markup = ToolStartFormatter.FormatMarkup(
            "file_access_read path=\"src/Case.cs\" staged=false in ~/work",
            includesToolName: true,
            includesWorkingDirectory: true
        );

        markup
            .Should()
            .Be(
                "[cornflowerblue]file_access_read[/][grey] [/][cyan]path[/][grey]=[/][yellow]\"src/Case.cs\"[/][grey] [/][cyan]staged[/][grey]=[/][yellow]false[/][grey] in [/][mediumpurple1]~/work[/]"
            );
    }

    [Fact]
    public void ToolStartsPreserveReadableSourceCharactersWhileEscapingLineBreaks()
    {
        var output = RenderTool(
            "file_access_replace",
            "{\"newString\":\"import { value } from \\\"./source\\\";\\nconst valid = value < 3 && value > 0;\"}",
            null
        );

        output
            .Should()
            .Contain(
                "newString=\"import { value } from \\\"./source\\\";\\nconst valid = value < 3 && value > 0;\""
            )
            .And.NotContain("\\u0022")
            .And.NotContain("\\u003C")
            .And.NotContain("\\u003E");
    }

    [Fact]
    public void ArgumentOrderIsStableAcrossProviderPropertyOrder()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "tandem-work");
        var first = RenderTool("custom", "{\"z\":1,\"a\":2}", workingDirectory)
            .Split('\n')
            .Single(line => line.Contains("custom a=2 z=1", StringComparison.Ordinal));
        var second = RenderTool("custom", "{\"a\":2,\"z\":1}", workingDirectory)
            .Split('\n')
            .Single(line => line.Contains("custom a=2 z=1", StringComparison.Ordinal));

        first.Should().Be(second);
    }

    [Fact]
    public void UndefinedArgumentsFromModelRenderOnlyToolNameAndWorkingDirectory()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "tandem-work");
        var console = new TestConsole().Width(180).Height(24);
        var model = new TerminalModel(
            "pipeline",
            _runId,
            TimeProvider.System,
            100,
            10_000,
            null,
            workingDirectory
        );
        model.Apply(
            new PipelineAgentUpdated(
                _runId,
                "executor",
                new AgentUpdate.ToolStarted("call", "search", default)
                {
                    WorkingDirectory = workingDirectory,
                }
            )
        );

        new TerminalRenderer(console).Render(model.Snapshot());

        console
            .Output.Should()
            .Contain("[executor] ↯")
            .And.Contain($"search in {workingDirectory}")
            .And.NotContain("search arguments=");
    }

    [Fact]
    public void ConfiguredToolsRenderOnlyToolNameAndWorkingDirectory()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "tandem-work");
        var console = new TestConsole().Width(180).Height(24);
        var model = new TerminalModel(
            "pipeline",
            _runId,
            TimeProvider.System,
            100,
            10_000,
            null,
            workingDirectory,
            new HashSet<string>(StringComparer.Ordinal) { "write_checkpoint" }
        );
        using var arguments = JsonDocument.Parse("{\"summary\":\"Detailed checkpoint\"}");
        model.Apply(
            new PipelineAgentUpdated(
                _runId,
                "executor",
                new AgentUpdate.ToolStarted("call", "write_checkpoint", arguments.RootElement)
                {
                    WorkingDirectory = workingDirectory,
                }
            )
        );

        new TerminalRenderer(console).Render(model.Snapshot());

        console
            .Output.Should()
            .Contain($"write_checkpoint in {workingDirectory}")
            .And.NotContain("Detailed checkpoint")
            .And.NotContain("summary=");
    }

    [Fact]
    public void ConfiguredArgumentOmissionIsExactAndDoesNotMutateTheObservation()
    {
        using var arguments = JsonDocument.Parse("{\"summary\":\"Complete evidence\"}");
        var tool = new AgentUpdate.ToolStarted("call", "semantic_tool", arguments.RootElement);
        var model = new TerminalModel(
            "pipeline",
            _runId,
            TimeProvider.System,
            100,
            10_000,
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal) { "semantic_tool" }
        );

        model.Apply(new PipelineAgentUpdated(_runId, "executor", tool));
        model.Apply(
            new PipelineAgentUpdated(
                _runId,
                "executor",
                new AgentUpdate.ToolStarted("case", "Semantic_tool", arguments.RootElement)
            )
        );
        model.Apply(
            new PipelineAgentUpdated(
                _runId,
                "executor",
                new AgentUpdate.ToolStarted("prefix", "semantic_tool_extra", arguments.RootElement)
            )
        );

        tool.Arguments.GetRawText().Should().Be("{\"summary\":\"Complete evidence\"}");
        model
            .Snapshot()
            .Transcript.Select(entry => entry.Text)
            .Should()
            .Equal(
                "",
                "{\"summary\":\"Complete evidence\"}",
                "{\"summary\":\"Complete evidence\"}"
            );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankWorkingDirectoryOmitsSuffix(string? workingDirectory)
    {
        var output = RenderTool("search", "{}", workingDirectory);
        output.Should().Contain("[executor] ↯").And.Contain("search");
        output.Should().NotContain("search in ");
    }

    [Fact]
    public void ToolStartsRetainTheirOwnWorkingDirectories()
    {
        var launch = Path.Combine(Path.GetTempPath(), "terminal-launch");
        var first = Path.Combine(Path.GetTempPath(), "agent-one");
        var second = Path.Combine(Path.GetTempPath(), "agent-two");
        var snapshot = ToolModel("read_ledger", "{}", first) with
        {
            Transcript =
            [
                new TranscriptEntry(
                    "planner",
                    TranscriptKind.ToolStarted,
                    "{\"cursor\":288,\"limit\":50}",
                    "read_ledger",
                    WorkingDirectory: first
                ),
                new TranscriptEntry(
                    "planner",
                    TranscriptKind.ToolStarted,
                    "{\"path\":\"src/Case.cs\"}",
                    "file_access_read",
                    WorkingDirectory: second
                ),
            ],
            WorkingDirectory = launch,
        };
        var console = new TestConsole().Width(220).Height(24);

        new TerminalRenderer(console).Render(snapshot);

        console.Output.Should().Contain($"read_ledger cursor=288 limit=50 in {first}");
        console.Output.Should().Contain($"file_access_read path=\"src/Case.cs\" in {second}");
        console.Output.Should().Contain(launch);
        console.Output.Should().NotContain($"read_ledger cursor=288 limit=50 in {launch}");
        console.Output.Should().NotContain($"file_access_read path=\"src/Case.cs\" in {launch}");
    }

    [Fact]
    public void WorkingDirectoryContractsOnlyHomeOrRealDescendants()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var descendant = Path.Combine(home, "Sites", "cadence");
        var prefixSibling = home + "-other";

        RenderTool("search", "{}", home).Should().Contain("search in ~");
        RenderTool("search", "{}", descendant)
            .Should()
            .Contain(
                $"search in ~{Path.DirectorySeparatorChar}Sites{Path.DirectorySeparatorChar}cadence"
            );
        RenderTool("search", "{}", prefixSibling).Should().Contain($"search in {prefixSibling}");
    }

    [Fact]
    public void LongToolArgumentsUseExistingWrappingAndScrollbackCapacity()
    {
        var console = new TestConsole().Width(80).Height(16);
        var renderer = new TerminalRenderer(console);
        var longValue = new string('x', 300);
        var entries = Enumerable
            .Range(0, 400)
            .Select(index => new TranscriptEntry(
                "executor",
                TranscriptKind.ToolStarted,
                $"{{\"value\":\"entry-{index:D4}-{longValue}\"}}",
                "custom"
            ))
            .ToArray();
        var snapshot = ToolModel("custom", "{}", null) with { Transcript = entries };

        renderer.Render(snapshot);
        console.Output.Should().Contain("entry-0399-");
        renderer.ScrollHome();
        renderer.Render(snapshot);

        var output = console.Output;
        output.Should().Contain("End follow").And.NotContain("entry-0000-");
        output
            .Split('\n')
            .Should()
            .Contain(line => line.Contains("custom", StringComparison.Ordinal))
            .And.Contain(line => line.Contains("value=\"entry-", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonWrappingPreservesEscapesAndStringStyleWithoutRecursiveParsing()
    {
        const string nested =
            """{"nested":"quote: \" and slash \\ and line \n and unicode \u263A"}""";
        var line = $"  \"message\": {JsonSerializer.Serialize($"Received: {nested}")}";

        var fragments = TerminalRenderer.WrapJsonLine(line, 14);
        var markup = string.Concat(fragments);
        var reconstructed = System.Text.RegularExpressions.Regex.Replace(markup, "\\[[^]]+\\]", "");

        reconstructed.Should().Be(line);
        fragments
            .Should()
            .OnlyContain(fragment => !fragment.Contains("[grey]nested", StringComparison.Ordinal));
        fragments
            .Should()
            .OnlyContain(fragment => !fragment.EndsWith("[green]\\[/]", StringComparison.Ordinal));
        markup.Should().Contain("[green]{[/]").And.NotContain("[cyan]nested[/]");
    }

    [Fact]
    public async Task InteractiveTranscriptIncludesToolAndCommandActivity()
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

    [Theory]
    [InlineData(TerminalPipelineStatus.Running, "14")]
    [InlineData(TerminalPipelineStatus.WaitingForInteraction, "11")]
    [InlineData(TerminalPipelineStatus.Succeeded, "2")]
    [InlineData(TerminalPipelineStatus.Failed, "9")]
    [InlineData(TerminalPipelineStatus.Faulted, "9")]
    [InlineData(TerminalPipelineStatus.Cancelled, "8")]
    public void HeaderUsesSemanticAnsiSegments(TerminalPipelineStatus status, string statusColor)
    {
        var snapshot = Model(("executor", "working")) with
        {
            Status = status,
            Title = "title[unsafe]",
        };
        var output = RenderAnsi(snapshot);

        output
            .Should()
            .Contain($"\u001b[38;5;69m{_runId:N}\u001b[0m")
            .And.Contain("\u001b[38;5;141mtitle[unsafe]\u001b[0m")
            .And.Contain($"\u001b[1;38;5;{statusColor}m{status}\u001b[0m")
            .And.MatchRegex("\\u001b\\[38;5;8m  00:00:0[0-9]\\u001b\\[0m");
    }

    [Fact]
    public void FooterUsesSemanticAnsiSegmentsForActionsCancellationAndWorkingDirectory()
    {
        var snapshot = Model(("executor", "working")) with { WorkingDirectory = "/work[unsafe]" };
        var output = RenderAnsi(
            snapshot,
            [new TerminalKeyAction(ConsoleKey.R, "retry[unsafe]", _ => ValueTask.CompletedTask)]
        );

        output
            .Should()
            .Contain("\u001b[38;5;14msteps\u001b[0m")
            .And.Contain("\u001b[38;5;15m 1\u001b[0m")
            .And.Contain("\u001b[38;5;11mr\u001b[0m")
            .And.Contain("\u001b[38;5;15m retry[unsafe]\u001b[0m")
            .And.Contain("\u001b[38;5;9m cancel\u001b[0m")
            .And.Contain("\u001b[38;5;141m/work[unsafe]\u001b[0m");
    }

    [Fact]
    public void FooterStylesScrollCloseAndInteractionSegments()
    {
        var entries = Enumerable
            .Range(0, 40)
            .Select(index => ("executor", $"line-{index}"))
            .ToArray();
        var console = new TestConsole()
            .Width(120)
            .Height(16)
            .Colors(ColorSystem.TrueColor)
            .EmitAnsiSequences();
        var renderer = new TerminalRenderer(console);
        renderer.Render(Model(entries));
        renderer.ScrollHome();
        renderer.Render(Model(entries));
        console
            .Output.Should()
            .MatchRegex("\\u001b\\[38;5;69m↑ [0-9]+ lines\\u001b\\[0m")
            .And.Contain("\u001b[38;5;11mEnd\u001b[0m");

        var terminal = Model(("executor", "done")) with
        {
            Status = TerminalPipelineStatus.Succeeded,
        };
        RenderAnsi(terminal).Should().Contain("\u001b[38;5;8m close\u001b[0m");

        var interaction = Model(("executor", "waiting")) with
        {
            Interaction = new TerminalInteractionPrompt("Question?"),
            Draft = "draft[unsafe]",
        };
        RenderAnsi(interaction)
            .Should()
            .Contain("\u001b[38;5;69m> \u001b[0m")
            .And.Contain("\u001b[38;5;15mdraft[unsafe]\u001b[0m")
            .And.Contain("\u001b[38;5;11m  Enter\u001b[0m")
            .And.Contain("\u001b[38;5;8m submit\u001b[0m");
    }

    [Fact]
    public void NarrowRenderedJsonKeepsTheEstablishedPaletteAcrossWrapping()
    {
        const string json = "{\"key\":[\"value: text\",true,false,12.5,null,{\"nested\":\"ok\"}]}";
        using var document = JsonDocument.Parse(json);
        var pretty = JsonSerializer.Serialize(
            document.RootElement,
            new JsonSerializerOptions { WriteIndented = true }
        );
        var snapshot = Model(("executor", "working")) with
        {
            Transcript = [new TranscriptEntry("executor", TranscriptKind.Semantic, json)],
        };
        var output = RenderAnsi(snapshot, width: 42);

        output
            .Should()
            .Contain("\u001b[38;5;14m\"key\"\u001b[0m")
            .And.Contain("\u001b[38;5;2m\"value: text\"\u001b[0m")
            .And.Contain("\u001b[38;5;11mtrue\u001b[0m")
            .And.Contain("\u001b[38;5;69m12.5\u001b[0m")
            .And.MatchRegex("\\u001b\\[38;5;8m +null");
        pretty.Should().Contain("value: text");
    }

    private static readonly Guid _runId = Guid.CreateVersion7();

    private static int PipelinePaneWidth(string output)
    {
        var border = output
            .Split('\n')
            .Single(line => line.Contains("Pipeline", StringComparison.Ordinal));
        return border.LastIndexOf('╮') - border.LastIndexOf('╭') + 1;
    }

    private static string RenderAnsi(
        TerminalSnapshot snapshot,
        IReadOnlyList<TerminalKeyAction>? actions = null,
        int width = 180
    )
    {
        var console = new TestConsole()
            .Width(width)
            .Height(30)
            .Colors(ColorSystem.TrueColor)
            .EmitAnsiSequences();
        new TerminalRenderer(console, actions).Render(snapshot);
        return console.Output;
    }

    private static string Render(params (string StepId, string Text)[] entries)
    {
        var console = new TestConsole().Width(120).Height(30);
        new TerminalRenderer(console).Render(Model(entries));
        return console.Output;
    }

    private static string RenderTool(string name, string arguments, string? workingDirectory)
    {
        var console = new TestConsole().Width(180).Height(24);
        new TerminalRenderer(console).Render(ToolModel(name, arguments, workingDirectory));
        return console.Output;
    }

    private static TerminalSnapshot ToolModel(
        string name,
        string arguments,
        string? workingDirectory
    )
    {
        using var document = JsonDocument.Parse(arguments);
        return Model(("executor", "working")) with
        {
            Transcript =
            [
                new TranscriptEntry(
                    "executor",
                    TranscriptKind.ToolStarted,
                    document.RootElement.GetRawText(),
                    name,
                    WorkingDirectory: workingDirectory
                ),
            ],
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "terminal-launch"),
        };
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
