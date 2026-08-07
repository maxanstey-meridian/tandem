using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Tandem.Actions;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;
using Tandem.Infrastructure.Lifecycle;

namespace Tandem.Tests.Infrastructure;

public sealed class LifecycleMcpTests
{
    private static AgentSessionDecision ContinueSession(DeliveryState _) =>
        new(AgentSessionAction.Continue, "Lifecycle fixture policy.");

    [Fact]
    public void ActionSetRegistry_ResolvesOnlyExplicitIdentity()
    {
        var deliverySelected = false;
        var debateSelected = false;
        var registry = new LifecycleActionSetRegistry(
            new(
                "delivery",
                services =>
                {
                    deliverySelected = true;
                    return services.AddMcpServer();
                }
            ),
            new(
                "debate",
                services =>
                {
                    debateSelected = true;
                    return services.AddMcpServer();
                }
            )
        );

        registry.Register(
            "debate",
            new Microsoft.Extensions.DependencyInjection.ServiceCollection()
        );

        debateSelected.Should().BeTrue();
        deliverySelected.Should().BeFalse();
        var act = () =>
            registry.Register(
                "unknown",
                new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            );
        act.Should().Throw<InvalidOperationException>().WithMessage("*not registered*");
    }

    [Fact]
    public async Task ReceiptStore_ParallelCreateOrRead_PublishesExactlyOneReceipt()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var store = new LifecycleReceiptStore(fixture.TandemHome);
        const string invocationId = "parallel-invocation";
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable
            .Range(0, 16)
            .Select(async index =>
            {
                await ready.Task;
                return await store.CreateOrReadAsync(
                    fixture.RunId,
                    invocationId,
                    "executor",
                    $"kind-{index}",
                    $"summary-{index}",
                    System.Text.Json.JsonSerializer.SerializeToElement(new { index }),
                    CancellationToken.None
                );
            })
            .ToArray();

        ready.SetResult();
        var results = await Task.WhenAll(callers);

        results.Count(result => result.Created).Should().Be(1);
        results
            .Select(result => result.Receipt.Kind)
            .Should()
            .OnlyContain(kind => kind == results[0].Receipt.Kind);
        results
            .Select(result => result.Receipt.Summary)
            .Should()
            .OnlyContain(summary => summary == results[0].Receipt.Summary);
        Directory
            .GetFiles(
                Path.Combine(fixture.TandemHome, "runs", fixture.RunId.ToString("N"), "lifecycle")
            )
            .Should()
            .ContainSingle(path => path.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LifecycleTools_AdvertiseFlatTypedSchemas()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        await using var client = new LifecycleMcpClient(
            fixture.TandemHome,
            fixture.TandemExePath,
            fixture.RunId,
            BlockIds.Executor,
            "schema-probe",
            DeliveryLifecycleActions.Identity
        );

        var tools = await client.ListToolsAsync(
            ["ask_planner", "submit_report", "write_checkpoint"],
            CancellationToken.None
        );

        tools
            .Select(tool => tool.Name)
            .Should()
            .BeEquivalentTo("ask_planner", "submit_report", "write_checkpoint");
        var askPlanner = tools.OfType<McpClientTool>().Single(tool => tool.Name == "ask_planner");
        var schema = askPlanner.JsonSchema;
        schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .BeEquivalentTo("question", "proposedApproach", "evidence");
        schema.GetProperty("properties").TryGetProperty("request", out _).Should().BeFalse();
        schema
            .GetProperty("properties")
            .GetProperty("evidence")
            .GetProperty("items")
            .GetProperty("type")
            .GetString()
            .Should()
            .Be("string");
    }

    [Fact]
    public async Task LifecycleTools_MissingRequestedTool_FailsImmediately()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        await using var client = new LifecycleMcpClient(
            fixture.TandemHome,
            fixture.TandemExePath,
            fixture.RunId,
            BlockIds.Executor,
            "missing-tool-probe",
            DeliveryLifecycleActions.Identity
        );

        var act = () => client.ListToolsAsync(["not_registered"], CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing requested tool(s): not_registered*");
    }

    [Fact]
    public async Task InvalidAskPlanner_ReturnsProblems_ThenAcceptsCorrectedCallInSameTurn()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var ctx = CreateMessage(
            fixture.RunId,
            MakePacket(),
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var toolResults = new List<string>();
        var script = new ScriptedChatClient(
            MakeToolCallResponse(
                "invalid-call",
                "ask_planner",
                new Dictionary<string, object?>
                {
                    ["question"] = "Should I proceed?",
                    ["proposedApproach"] = "",
                    ["evidence"] = new[] { "README.md" },
                }
            ),
            MakeToolCallResponse(
                "corrected-call",
                "ask_planner",
                new Dictionary<string, object?>
                {
                    ["question"] = "Should I proceed?",
                    ["proposedApproach"] = "Apply the requested focused change.",
                    ["evidence"] = new[] { "README.md" },
                }
            )
        );
        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                ["ask_planner"],
                _ => "test message",
                state => state.WorkspacePath,
                _ => false,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath,
            onUpdate: (_, _, update) =>
            {
                if (update is AgentUpdate.ToolCompleted result)
                {
                    toolResults.Add(result.Result ?? "");
                }
            }
        );

        var output = await RunBlockAsync(block, ctx);

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.PlannerRequested);
        toolResults.Should().Contain(result => result.Contains("invalid ask_planner call"));
        toolResults.Should().Contain(result => result.Contains("proposedApproach"));
        var lifecycleDirectory = Path.Combine(
            fixture.TandemHome,
            "runs",
            fixture.RunId.ToString("N"),
            "lifecycle"
        );
        Directory.GetFiles(lifecycleDirectory, "*.json").Should().ContainSingle();
    }

    [Fact]
    public async Task AskPlanner_AcceptsReceipt_TerminatesTurn_RoutesToPlanner()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var packet = MakePacket();

        var ctx = CreateMessage(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );

        var script = new ScriptedChatClient(
            MakeToolCallResponse(
                "call-1",
                "ask_planner",
                new Dictionary<string, object?>
                {
                    ["question"] = "Should I add a README?",
                    ["proposedApproach"] = "Create README.md with project overview.",
                    ["evidence"] = new[] { "src/Program.cs" },
                }
            ),
            MakeToolCallResponse(
                "call-2",
                "file_access_write",
                new Dictionary<string, object?>
                {
                    ["fileName"] = "should-not-exist.txt",
                    ["content"] = "must not be written after termination",
                }
            ),
            MakeTextResponse("I tried to write after the lifecycle call.")
        );

        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                ["ask_planner", "submit_report", "write_checkpoint"],
                _ => "test message",
                state => state.WorkspacePath,
                _ => false,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );

        var binding = block.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();

        var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            ctx,
            fixture.RunId.ToString("N"),
            CancellationToken.None
        );

        var events = new List<WorkflowEvent>();
        PipelineMessage<DeliveryState>? output = null;
        Exception? failure = null;
        await foreach (var evt in runHandle.WatchStreamAsync(CancellationToken.None))
        {
            events.Add(evt);
            if (evt is WorkflowErrorEvent errorEvent)
            {
                failure = errorEvent.Exception;
            }
            else if (evt is ExecutorFailedEvent failedEvent)
            {
                failure = failedEvent.Data;
            }
            else if (evt is WorkflowOutputEvent oe && oe.Is<PipelineMessage<DeliveryState>>())
            {
                output = oe.As<PipelineMessage<DeliveryState>>();
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException($"Block failed: {failure.Message}", failure);
        }

        output.Should().NotBeNull("the block must produce a pipeline message");
        output!
            .LatestOutcome!.Kind.Should()
            .Be(OutcomeKinds.PlannerRequested, "ask_planner should produce planner.requested");

        var receiptPath = Path.Combine(
            fixture.TandemHome,
            "runs",
            fixture.RunId.ToString("N"),
            "lifecycle",
            $"{ctx.Runtime.NextInvocationId(BlockIds.Executor)}.json"
        );
        File.Exists(receiptPath).Should().BeTrue("the receipt must be persisted");
        var receipt = await File.ReadAllTextAsync(receiptPath);
        receipt.Should().Contain(OutcomeKinds.PlannerRequested);
        receipt.Should().Contain("Should I add a README?");

        File.Exists(Path.Combine(fixture.WorkspacePath, "should-not-exist.txt"))
            .Should()
            .BeFalse("the later file write must not run after termination");

        events
            .OfType<AgentResponseUpdateEvent>()
            .SelectMany(e => e.Update.Contents.OfType<TextContent>())
            .Should()
            .NotContain(
                c => c.Text.Contains("tried to write after"),
                "post-lifecycle assistant text must not appear as a run event"
            );

        events
            .Select(e => e.ToString())
            .Should()
            .NotContain(
                s => s.Contains("jsonrpc", StringComparison.OrdinalIgnoreCase),
                "MCP protocol stdout must never appear as a run event"
            );
    }

    [Fact]
    public async Task ExistingReceipt_SkipsModel_AndReturnsOutcome()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var packet = MakePacket();

        var ctx = CreateMessage(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var invocationId = ctx.Runtime.NextInvocationId(BlockIds.Executor);

        var store = new LifecycleReceiptStore(fixture.TandemHome);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new
            {
                question = "pre-seeded",
                proposedApproach = "n/a",
                evidence = Array.Empty<string>(),
            }
        );
        await store.WriteAsync(
            fixture.RunId,
            invocationId,
            BlockIds.Executor,
            OutcomeKinds.PlannerRequested,
            "Pre-seeded receipt",
            payload,
            CancellationToken.None
        );

        var script = new ScriptedChatClient(
            MakeTextResponse("This response must never be produced.")
        );

        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                ["ask_planner", "submit_report", "write_checkpoint"],
                _ => "test message",
                state => state.WorkspacePath,
                _ => false,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession
            ),
            script,
            fixture.TandemHome,
            Path.Combine(fixture.TandemHome, "must-not-spawn")
        );

        var binding = block.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();

        var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            ctx,
            fixture.RunId.ToString("N"),
            CancellationToken.None
        );

        PipelineMessage<DeliveryState>? output = null;
        await foreach (var evt in runHandle.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowOutputEvent oe && oe.Is<PipelineMessage<DeliveryState>>())
            {
                output = oe.As<PipelineMessage<DeliveryState>>();
            }
        }

        output.Should().NotBeNull();
        output!
            .LatestOutcome!.Kind.Should()
            .Be(OutcomeKinds.PlannerRequested, "the seeded receipt must be returned");
        output.LatestOutcome.Summary.Should().Be("Pre-seeded receipt");
    }

    [Fact]
    public async Task ExistingSubmitReportReceipt_AppliesTransition_WithoutModelOrProcess()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var message = CreateMessage(
            fixture.RunId,
            MakePacket(),
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new
            {
                summary = "implemented",
                outcomes = new[] { "o1" },
                evidence = new[] { "src/change.cs" },
            }
        );
        var sessionPath = Path.Combine(
            fixture.TandemHome,
            "runs",
            fixture.RunId.ToString("N"),
            "sessions",
            $"{BlockIds.Executor}.json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        var invocationId = message.Runtime.NextInvocationId(BlockIds.Executor);
        await File.WriteAllTextAsync(
            sessionPath,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    invocationId,
                    session = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
                }
            )
        );
        await new LifecycleReceiptStore(fixture.TandemHome).WriteAsync(
            fixture.RunId,
            invocationId,
            BlockIds.Executor,
            OutcomeKinds.ReportSubmitted,
            "Implemented",
            payload,
            CancellationToken.None
        );
        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor",
                ["submit_report"],
                _ => "must not execute",
                state => state.WorkspacePath,
                _ => false,
                ReceiptTransition: (state, kind, acceptedPayload) =>
                    kind == OutcomeKinds.ReportSubmitted
                        ? state with
                        {
                            ImplementationReport = acceptedPayload,
                        }
                        : state,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession,
                ProfilePolicy: _ => new AgentProfileDecision("promoted", "Replay proof."),
                TeardownPolicy: (_, _) =>
                    new AgentTeardownDecision(true, true, "Accepted report closes session.")
            ),
            new ScriptedChatClient(MakeTextResponse("must not execute")),
            fixture.TandemHome,
            Path.Combine(fixture.TandemHome, "must-not-spawn")
        );

        var output = await block.ExecuteAsync(message, CancellationToken.None);

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.ReportSubmitted);
        System
            .Text.Json.JsonElement.DeepEquals(output.State.ImplementationReport!.Value, payload)
            .Should()
            .BeTrue();
        output.Runtime.InvocationCounts[BlockIds.Executor].Should().Be(1);
        output.Runtime.AgentProfiles[BlockIds.Executor].ProfileName.Should().Be("promoted");
        var persistedProfile = await File.ReadAllTextAsync(
            Path.Combine(
                fixture.TandemHome,
                "runs",
                fixture.RunId.ToString("N"),
                "profiles",
                $"{BlockIds.Executor}.json"
            )
        );
        persistedProfile.Should().Contain("promoted");
        output.Runtime.AgentSessions.Should().NotContainKey(BlockIds.Executor);
        output.Runtime.AgentUsage.Should().NotContainKey(BlockIds.Executor);
        File.Exists(sessionPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExistingReceipt_WithoutMatchingPersistedInvocation_DropsStaleSession()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var message = CreateMessage(
            fixture.RunId,
            MakePacket(),
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        message = message with
        {
            Runtime = message.Runtime.WithSession(
                BlockIds.Executor,
                System.Text.Json.JsonSerializer.SerializeToElement(
                    new { invocationId = "prior-invocation" }
                )
            ),
        };
        var sessionPath = Path.Combine(
            fixture.TandemHome,
            "runs",
            fixture.RunId.ToString("N"),
            "sessions",
            $"{BlockIds.Executor}.json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(
            sessionPath,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    invocationId = "prior-invocation",
                    session = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
                }
            )
        );
        var orphanTempPath = $"{sessionPath}.orphan.tmp";
        await File.WriteAllTextAsync(orphanTempPath, "stale");
        var invocationId = message.Runtime.NextInvocationId(BlockIds.Executor);
        await new LifecycleReceiptStore(fixture.TandemHome).WriteAsync(
            fixture.RunId,
            invocationId,
            BlockIds.Executor,
            OutcomeKinds.PlannerRequested,
            "Planner requested",
            System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );
        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor",
                ["ask_planner"],
                _ => "must not execute",
                state => state.WorkspacePath,
                _ => false,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession
            ),
            new ScriptedChatClient(MakeTextResponse("must not execute")),
            fixture.TandemHome,
            Path.Combine(fixture.TandemHome, "must-not-spawn")
        );

        var output = await block.ExecuteAsync(message, CancellationToken.None);

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.PlannerRequested);
        output.Runtime.AgentSessions.Should().NotContainKey(BlockIds.Executor);
        File.Exists(sessionPath).Should().BeFalse();
        File.Exists(orphanTempPath).Should().BeFalse();
    }

    [Fact]
    public async Task LifecycleActions_RequireExplicitActionSetIdentity()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var message = CreateMessage(fixture.RunId, MakePacket(), "abc123", fixture.WorkspacePath);
        message = message with
        {
            Runtime = message.Runtime.WithSession(
                BlockIds.Executor,
                System.Text.Json.JsonSerializer.SerializeToElement(true)
            ),
        };
        var sessionPath = Path.Combine(
            fixture.TandemHome,
            "runs",
            fixture.RunId.ToString("N"),
            "sessions",
            $"{BlockIds.Executor}.json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(sessionPath, "{}");
        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor",
                ["ask_planner"],
                _ => "message",
                state => state.WorkspacePath,
                _ => false,
                SessionPolicy: _ => new AgentSessionDecision(
                    AgentSessionAction.Reset,
                    "Reset fixture session."
                )
            ),
            new ScriptedChatClient(MakeTextResponse("must not execute")),
            fixture.TandemHome,
            fixture.TandemExePath
        );

        var act = () => block.ExecuteAsync(message, CancellationToken.None).AsTask();

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*must explicitly select a lifecycle action set*");
        File.Exists(sessionPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExistingCheckpointReceipt_AppliesTransitionAndClearsExecutorRuntime()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var message = CreateMessage(
            fixture.RunId,
            MakePacket(),
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var invocationId = message.Runtime.NextInvocationId(BlockIds.Executor);
        var runtime = message
            .Runtime.WithSession(
                BlockIds.Executor,
                System.Text.Json.JsonSerializer.SerializeToElement(new { invocationId })
            )
            .WithUsage(BlockIds.Executor, new AgentUsage(10, 5, 15, 100, 80, TimeSpan.Zero));
        message = message with { Runtime = runtime };
        var sessionPath = Path.Combine(
            fixture.TandemHome,
            "runs",
            fixture.RunId.ToString("N"),
            "sessions",
            $"{BlockIds.Executor}.json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(
            sessionPath,
            System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    invocationId,
                    session = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
                }
            )
        );
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new
            {
                summary = "saved",
                completed = Array.Empty<string>(),
                next = new[] { "finish" },
            }
        );
        await new LifecycleReceiptStore(fixture.TandemHome).WriteAsync(
            fixture.RunId,
            invocationId,
            BlockIds.Executor,
            OutcomeKinds.CheckpointWritten,
            "Checkpoint written: saved",
            payload,
            CancellationToken.None
        );
        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor",
                ["write_checkpoint"],
                _ => "message",
                state => state.WorkspacePath,
                _ => true,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession,
                Checkpoint: new CheckpointPolicy<DeliveryState>(
                    100,
                    10,
                    80,
                    "write_checkpoint",
                    OutcomeKinds.CheckpointWritten,
                    "checkpoint",
                    _ => "checkpoint",
                    (state, _, acceptedPayload) =>
                        state with
                        {
                            CheckpointPayload = acceptedPayload,
                        }
                )
            ),
            new ScriptedChatClient(MakeTextResponse("must not execute")),
            fixture.TandemHome,
            fixture.TandemExePath
        );

        var output = await block.HandleAsync(
            message,
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.State.CheckpointPayload.Should().NotBeNull();
        output.Runtime.AgentSessions.Should().NotContainKey(BlockIds.Executor);
        output.Runtime.AgentUsage.Should().NotContainKey(BlockIds.Executor);
        output.Runtime.InvocationCounts[BlockIds.Executor].Should().Be(1);
        File.Exists(sessionPath).Should().BeFalse();
    }

    [Fact]
    public async Task SubmitReport_AcceptsReceipt_RoutesToCaptureCandidate()
    {
        using var fixture = await LifecycleFixture.CreateAsync(initGitWorkspace: true);
        var packet = MakePacket();

        var ctx = CreateMessage(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );

        var script = new ScriptedChatClient(
            MakeToolCallResponse(
                "call-1",
                "submit_report",
                new Dictionary<string, object?>
                {
                    ["summary"] = "Implemented the greeting file.",
                    ["outcomes"] = new[] { "greeting: created greeting.txt" },
                    ["evidence"] = new[] { "greeting.txt" },
                }
            ),
            MakeToolCallResponse(
                "call-2",
                "file_access_write",
                new Dictionary<string, object?>
                {
                    ["fileName"] = "post-submit.txt",
                    ["content"] = "must not run after termination",
                }
            ),
            MakeTextResponse("I tried to write after the lifecycle call.")
        );

        var executor = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                ["ask_planner", "submit_report", "write_checkpoint"],
                _ => "test message",
                state => state.WorkspacePath,
                _ => false,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );
        var capture = new CaptureCandidateBlock(new GitProcess());

        var executorBinding = executor.BindExecutor();
        var captureBinding = new CaptureCandidateTestExecutor(capture).BindExecutor();

        var builder = new WorkflowBuilder(executorBinding);
        builder = builder.AddEdge<PipelineMessage<DeliveryState>>(
            executorBinding,
            captureBinding,
            msg => msg!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
        );
        builder = builder.WithOutputFrom(captureBinding);
        var workflow = builder.Build();

        var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            ctx,
            fixture.RunId.ToString("N"),
            CancellationToken.None
        );

        var events = new List<WorkflowEvent>();
        PipelineMessage<DeliveryState>? output = null;
        Exception? failure = null;
        await foreach (var evt in runHandle.WatchStreamAsync(CancellationToken.None))
        {
            events.Add(evt);
            if (evt is WorkflowErrorEvent errorEvent)
            {
                failure = errorEvent.Exception;
            }
            else if (evt is ExecutorFailedEvent failedEvent)
            {
                failure = failedEvent.Data;
            }
            else if (evt is WorkflowOutputEvent oe && oe.Is<PipelineMessage<DeliveryState>>())
            {
                output = oe.As<PipelineMessage<DeliveryState>>();
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException($"Workflow failed: {failure.Message}", failure);
        }

        output.Should().NotBeNull("the workflow must produce output");
        output!
            .LatestOutcome!.Kind.Should()
            .Be(OutcomeKinds.CandidateCaptured, "submit_report should route to capture-candidate");
        output.State.CandidateSha.Should().NotBeNullOrEmpty("the candidate SHA must be set");

        File.Exists(Path.Combine(fixture.WorkspacePath, "post-submit.txt"))
            .Should()
            .BeFalse("the later file write must not run after termination");

        events
            .OfType<AgentResponseUpdateEvent>()
            .SelectMany(e => e.Update.Contents.OfType<TextContent>())
            .Should()
            .NotContain(
                c => c.Text.Contains("tried to write after"),
                "post-lifecycle assistant text must not appear as a run event"
            );
    }

    [Fact]
    public async Task Cancellation_WhileMcpCallActive_FailsBlockAndLeavesNoChild()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var childExeName = Path.GetFileNameWithoutExtension(fixture.TandemExePath);
        var existingProcessIds = System
            .Diagnostics.Process.GetProcessesByName(childExeName)
            .Select(process => process.Id)
            .ToHashSet();
        var packet = MakePacket();

        var ctx = CreateMessage(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );

        var script = new CancelableScriptedChatClient(
            MakeToolCallResponse(
                "call-1",
                "ask_planner",
                new Dictionary<string, object?>
                {
                    ["question"] = "slow question",
                    ["proposedApproach"] = "n/a",
                    ["evidence"] = Array.Empty<string>(),
                }
            )
        );

        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                ["ask_planner", "submit_report", "write_checkpoint"],
                _ => "test message",
                state => state.WorkspacePath,
                _ => false,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );

        using var cts = new CancellationTokenSource();
        var handleTask = block.HandleAsync(ctx, new NoOpWorkflowContext(), cts.Token).AsTask();

        await Task.Delay(TimeSpan.FromSeconds(6));
        cts.Cancel();

        Exception? caught = null;
        try
        {
            await handleTask;
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("cancellation must surface as an exception from the block");
        caught
            .Should()
            .BeAssignableTo<OperationCanceledException>(
                "the exception should carry the cancellation reason"
            );

        await Task.Delay(TimeSpan.FromSeconds(1));
        var childrenAfter = System
            .Diagnostics.Process.GetProcessesByName(childExeName)
            .Where(process => !existingProcessIds.Contains(process.Id));
        childrenAfter
            .Should()
            .BeEmpty("no MCP child process should be left running after cancellation");
    }

    [Fact]
    public async Task ProseTurn_UsesComposedContinuation_AndReachesPlanner()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var packet = MakePacket();
        var ctx = CreateMessage(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var continuationAttempts = new List<int>();
        var script = new ScriptedChatClient(
            MakeTextResponse("I inspected the repository and will continue."),
            MakeToolCallResponse(
                "call-1",
                "ask_planner",
                new Dictionary<string, object?>
                {
                    ["question"] = "Should I add the requested change?",
                    ["proposedApproach"] = "Implement the packet outcome after approval.",
                    ["evidence"] = new[] { "README.md" },
                }
            )
        );

        var policy = new AgentTurnPolicy<DeliveryState>(
            1,
            (observation, _) =>
            {
                continuationAttempts.Add(observation.ContinuationAttempt);
                return ValueTask.FromResult<AgentTurnDirective?>(
                    new AgentTurnDirective(
                        "Call ask_planner now with your proposed approach and evidence.",
                        "ask_planner"
                    )
                );
            }
        );
        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                ["ask_planner", "submit_report", "write_checkpoint"],
                _ => "test message",
                state => state.WorkspacePath,
                _ => false,
                LifecycleActionSetIdentity: "delivery",
                TurnPolicy: policy,
                SessionPolicy: ContinueSession
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );

        var output = await RunBlockAsync(block, ctx);

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.PlannerRequested);
        continuationAttempts.Should().Equal(0);
    }

    [Fact]
    public async Task BlockedMutation_ReturnsComposedWarning_ThenContinuesToPlanner()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var packet = MakePacket();
        var ctx = CreateMessage(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var toolResults = new List<string>();
        const string warning =
            "COMPOSED GATE: mutation authority is closed; call ask_planner before editing.";
        var script = new ScriptedChatClient(
            MakeToolCallResponse(
                "call-1",
                "file_access_write",
                new Dictionary<string, object?>
                {
                    ["fileName"] = "blocked.txt",
                    ["content"] = "must not be written",
                }
            ),
            MakeTextResponse("I received the tool result."),
            MakeToolCallResponse(
                "call-2",
                "ask_planner",
                new Dictionary<string, object?>
                {
                    ["question"] = "May I make the requested edit?",
                    ["proposedApproach"] = "Apply the packet change after approval.",
                    ["evidence"] = new[] { "README.md" },
                }
            )
        );
        ToolInterceptor<DeliveryState> interceptor = (context, invocation, _) =>
        {
            if (
                context.State.MutationAuthorized
                || !invocation.Name.StartsWith("file_access_", StringComparison.Ordinal)
                || invocation.Name == "file_access_read"
            )
            {
                return ValueTask.FromResult<ToolInterceptionResult?>(null);
            }

            return ValueTask.FromResult<ToolInterceptionResult?>(
                new ToolInterceptionResult.Blocked(warning)
            );
        };
        var policy = new AgentTurnPolicy<DeliveryState>(
            1,
            (_, _) =>
                ValueTask.FromResult<AgentTurnDirective?>(
                    new AgentTurnDirective("Call ask_planner now.", "ask_planner")
                )
        );
        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                ["ask_planner", "submit_report", "write_checkpoint"],
                _ => "test message",
                state => state.WorkspacePath,
                _ => false,
                LifecycleActionSetIdentity: "delivery",
                SessionPolicy: ContinueSession,
                TurnPolicy: policy
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath,
            onUpdate: (_, _, update) =>
            {
                if (update is AgentUpdate.ToolCompleted result)
                {
                    toolResults.Add(result.Result ?? "");
                }
            },
            toolInterceptor: interceptor
        );

        var output = await RunBlockAsync(block, ctx);

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.PlannerRequested);
        File.Exists(Path.Combine(fixture.WorkspacePath, "blocked.txt")).Should().BeFalse();
        toolResults.Should().Contain(warning);
    }

    [Fact]
    public async Task ProseOnlyTurns_FailAtConfiguredContinuationBound()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var packet = MakePacket();
        var ctx = CreateMessage(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var script = new ScriptedChatClient(
            MakeTextResponse("I inspected the repository."),
            MakeTextResponse("I am still describing the repository.")
        );
        var policy = new AgentTurnPolicy<DeliveryState>(
            1,
            (_, _) =>
                ValueTask.FromResult<AgentTurnDirective?>(
                    new AgentTurnDirective("Continue, but do not finish in prose.")
                )
        );
        var block = new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                [],
                _ => "test message",
                state => state.WorkspacePath,
                _ => false,
                TurnPolicy: policy,
                SessionPolicy: ContinueSession
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );

        var output = await RunBlockAsync(block, ctx);

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.LatestOutcome.Summary.Should().Contain("2 model turn(s)");
    }

    private static async Task<PipelineMessage<DeliveryState>> RunBlockAsync(
        AgentBlock<DeliveryState> block,
        PipelineMessage<DeliveryState> message
    )
    {
        var binding = block.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();
        await using var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            message,
            message.Runtime.RunId.ToString("N"),
            CancellationToken.None
        );

        PipelineMessage<DeliveryState>? output = null;
        Exception? failure = null;
        await foreach (var evt in runHandle.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowErrorEvent errorEvent)
            {
                failure = errorEvent.Exception;
            }
            else if (evt is ExecutorFailedEvent failedEvent)
            {
                failure = failedEvent.Data;
            }
            else if (
                evt is WorkflowOutputEvent outputEvent
                && outputEvent.Is<PipelineMessage<DeliveryState>>()
            )
            {
                output = outputEvent.As<PipelineMessage<DeliveryState>>();
            }
        }

        failure.Should().BeNull("the block workflow should not throw");
        return output ?? throw new InvalidOperationException("The block produced no output.");
    }

    private static PipelineMessage<DeliveryState> CreateMessage(
        Guid runId,
        Packet packet,
        string pinnedBaseSha,
        string workspacePath
    ) =>
        new(
            PipelineRuntime.Create(runId),
            DeliveryState.Create(packet, pinnedBaseSha, workspacePath)
        );

    private static Packet MakePacket() =>
        new(
            Title: "Test packet",
            Repository: "file:///nonexistent",
            Base: "main",
            Outcomes: [new Outcome("o1", "Do the thing.")],
            Verification: [],
            Constraints: [],
            ImplementationContext: ""
        );

    private static ChatResponse MakeToolCallResponse(
        string callId,
        string toolName,
        IDictionary<string, object?> arguments
    ) =>
        new(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, toolName, arguments)]
            )
        )
        {
            FinishReason = ChatFinishReason.ToolCalls,
            ModelId = "test-model",
        };

    private static ChatResponse MakeTextResponse(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Dequeue());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var response = Dequeue();
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        private ChatResponse Dequeue()
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("ScriptedChatClient exhausted.");
            }

            return _responses.Dequeue();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class CancelableScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly ChatResponse[] _responses = responses;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responses[0]);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var response = _responses[0];
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

internal sealed class CaptureCandidateTestExecutor(CaptureCandidateBlock operation)
    : Executor<PipelineMessage<DeliveryState>, PipelineMessage<DeliveryState>>(
        BlockIds.CaptureCandidate
    )
{
    public override ValueTask<PipelineMessage<DeliveryState>> HandleAsync(
        PipelineMessage<DeliveryState> message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    ) => operation.ExecuteAsync(message, cancellationToken);
}

internal sealed class LifecycleFixture : IDisposable
{
    public string TandemHome { get; }
    public string WorkspacePath { get; }
    public Guid RunId { get; }
    public string TandemExePath { get; }

    private LifecycleFixture(
        string tandemHome,
        string workspacePath,
        Guid runId,
        string tandemExePath
    )
    {
        TandemHome = tandemHome;
        WorkspacePath = workspacePath;
        RunId = runId;
        TandemExePath = tandemExePath;
    }

    public static Task<LifecycleFixture> CreateAsync(bool initGitWorkspace = false)
    {
        var tandemHome = Path.Combine(
            Path.GetTempPath(),
            "tandem-home-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tandemHome);

        var workspacePath = Path.Combine(
            Path.GetTempPath(),
            "tandem-ws-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(workspacePath);

        if (initGitWorkspace)
        {
            InitGitRepo(workspacePath);
        }

        var tandemExePath = ResolveTandemExePath();

        return Task.FromResult(
            new LifecycleFixture(tandemHome, workspacePath, Guid.CreateVersion7(), tandemExePath)
        );
    }

    private static void InitGitRepo(string path)
    {
        var git = Environment.GetEnvironmentVariable("TANDEM_TEST_GIT") ?? "git";
        RunGit(git, path, ["init", "-q"]);
        RunGit(git, path, ["config", "user.email", "tandem@test.local"]);
        RunGit(git, path, ["config", "user.name", "Tandem Test"]);
        File.WriteAllText(Path.Combine(path, "anchor.txt"), "anchor\n");
        RunGit(git, path, ["add", "-A"]);
        RunGit(git, path, ["commit", "-qm", "init"]);
        RunGit(git, path, ["branch", "-m", "main"]);
    }

    private static void RunGit(string git, string workingDir, string[] args)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = git,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            },
        };
        foreach (var a in args)
        {
            p.StartInfo.ArgumentList.Add(a);
        }

        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        stdoutTask.Wait();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed: exit {p.ExitCode}"
            );
        }
    }

    private static string ResolveTandemExePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tandem"),
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "Tandem.Tool",
                "bin",
                "Debug",
                "net10.0",
                "Tandem.Tool"
            ),
        };

        foreach (var candidate in candidates)
        {
            var resolved = Path.GetFullPath(candidate);
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        throw new FileNotFoundException(
            "Could not locate the Tandem executable. Ensure the Tandem project is built."
        );
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(TandemHome, recursive: true);
        }
        catch { }
        try
        {
            Directory.Delete(WorkspacePath, recursive: true);
        }
        catch { }
    }
}
