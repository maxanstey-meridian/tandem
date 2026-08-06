using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;
using Tandem.Infrastructure.Lifecycle;

namespace Tandem.Tests.Infrastructure;

public sealed class LifecycleMcpTests
{
    [Fact]
    public async Task LifecycleTools_AdvertiseFlatTypedSchemas()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        await using var client = new LifecycleMcpClient(
            fixture.TandemHome,
            fixture.TandemExePath,
            fixture.RunId,
            BlockIds.Executor,
            "schema-probe"
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
    public async Task InvalidAskPlanner_ReturnsProblems_ThenAcceptsCorrectedCallInSameTurn()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var ctx = PipelineContext.Create(
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
        var block = new AgentBlock(
            new AgentBlockConfig(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                WorkspaceAccess.MutationGated,
                ["ask_planner"]
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath,
            onUpdate: (_, _, update) =>
            {
                foreach (var result in update.Contents.OfType<FunctionResultContent>())
                {
                    toolResults.Add(result.Result?.ToString() ?? "");
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

        var ctx = PipelineContext.Create(
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

        var block = new AgentBlock(
            new AgentBlockConfig(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                WorkspaceAccess.MutationGated,
                ["ask_planner", "submit_report", "write_checkpoint"]
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );

        var binding = block.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();

        var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            new PipelineMessage(ctx),
            fixture.RunId.ToString("N"),
            CancellationToken.None
        );

        var events = new List<WorkflowEvent>();
        PipelineMessage? output = null;
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
            else if (evt is WorkflowOutputEvent oe && oe.Is<PipelineMessage>())
            {
                output = oe.As<PipelineMessage>();
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
            $"{ctx.NextInvocationId(BlockIds.Executor)}.json"
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

        var ctx = PipelineContext.Create(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var invocationId = ctx.NextInvocationId(BlockIds.Executor);

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

        var block = new AgentBlock(
            new AgentBlockConfig(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                WorkspaceAccess.MutationGated,
                ["ask_planner", "submit_report", "write_checkpoint"]
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );

        var binding = block.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();

        var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            new PipelineMessage(ctx),
            fixture.RunId.ToString("N"),
            CancellationToken.None
        );

        PipelineMessage? output = null;
        await foreach (var evt in runHandle.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowOutputEvent oe && oe.Is<PipelineMessage>())
            {
                output = oe.As<PipelineMessage>();
            }
        }

        output.Should().NotBeNull();
        output!
            .LatestOutcome!.Kind.Should()
            .Be(OutcomeKinds.PlannerRequested, "the seeded receipt must be returned");
        output.LatestOutcome.Summary.Should().Be("Pre-seeded receipt");
    }

    [Fact]
    public async Task SubmitReport_AcceptsReceipt_RoutesToCaptureCandidate()
    {
        using var fixture = await LifecycleFixture.CreateAsync(initGitWorkspace: true);
        var packet = MakePacket();

        var ctx = PipelineContext.Create(
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

        var executor = new AgentBlock(
            new AgentBlockConfig(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                WorkspaceAccess.MutationGated,
                ["ask_planner", "submit_report", "write_checkpoint"]
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );
        var capture = new CaptureCandidateBlock();

        var executorBinding = executor.BindExecutor();
        var captureBinding = capture.BindExecutor();

        var builder = new WorkflowBuilder(executorBinding);
        builder = builder.AddEdge<PipelineMessage>(
            executorBinding,
            captureBinding,
            msg => msg!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
        );
        builder = builder.WithOutputFrom(captureBinding);
        var workflow = builder.Build();

        var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            new PipelineMessage(ctx),
            fixture.RunId.ToString("N"),
            CancellationToken.None
        );

        var events = new List<WorkflowEvent>();
        PipelineMessage? output = null;
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
            else if (evt is WorkflowOutputEvent oe && oe.Is<PipelineMessage>())
            {
                output = oe.As<PipelineMessage>();
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
        output.Context.CandidateSha.Should().NotBeNullOrEmpty("the candidate SHA must be set");

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

        var ctx = PipelineContext.Create(
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

        var block = new AgentBlock(
            new AgentBlockConfig(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                WorkspaceAccess.MutationGated,
                ["ask_planner", "submit_report", "write_checkpoint"]
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );

        using var cts = new CancellationTokenSource();
        var handleTask = block
            .HandleAsync(new PipelineMessage(ctx), new NoOpWorkflowContext(), cts.Token)
            .AsTask();

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
        var ctx = PipelineContext.Create(
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

        var policy = new AgentTurnPolicy(
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
        var block = new AgentBlock(
            new AgentBlockConfig(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                WorkspaceAccess.MutationGated,
                ["ask_planner", "submit_report", "write_checkpoint"],
                TurnPolicy: policy
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
        var ctx = PipelineContext.Create(
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
        ToolInterceptor interceptor = (context, invocation, _) =>
        {
            if (
                context.MutationAuthorized
                || !invocation.Function.Name.StartsWith("file_access_", StringComparison.Ordinal)
                || invocation.Function.Name == "file_access_read"
            )
            {
                return ValueTask.FromResult<ToolInterceptionResult?>(null);
            }

            return ValueTask.FromResult<ToolInterceptionResult?>(
                new ToolInterceptionResult.Blocked(warning)
            );
        };
        var policy = new AgentTurnPolicy(
            1,
            (_, _) =>
                ValueTask.FromResult<AgentTurnDirective?>(
                    new AgentTurnDirective("Call ask_planner now.", "ask_planner")
                )
        );
        var block = new AgentBlock(
            new AgentBlockConfig(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                WorkspaceAccess.MutationGated,
                ["ask_planner", "submit_report", "write_checkpoint"],
                TurnPolicy: policy
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath,
            onUpdate: (_, _, update) =>
            {
                foreach (var result in update.Contents.OfType<FunctionResultContent>())
                {
                    toolResults.Add(result.Result?.ToString() ?? "");
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
        var ctx = PipelineContext.Create(
            fixture.RunId,
            packet,
            pinnedBaseSha: "abc123",
            workspacePath: fixture.WorkspacePath
        );
        var script = new ScriptedChatClient(
            MakeTextResponse("I inspected the repository."),
            MakeTextResponse("I am still describing the repository.")
        );
        var policy = new AgentTurnPolicy(
            1,
            (_, _) =>
                ValueTask.FromResult<AgentTurnDirective?>(
                    new AgentTurnDirective("Continue, but do not finish in prose.")
                )
        );
        var block = new AgentBlock(
            new AgentBlockConfig(
                BlockIds.Executor,
                "implementation",
                "executor instructions",
                WorkspaceAccess.ReadOnly,
                [],
                TurnPolicy: policy
            ),
            script,
            fixture.TandemHome,
            fixture.TandemExePath
        );

        var output = await RunBlockAsync(block, ctx);

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.LatestOutcome.Summary.Should().Contain("2 model turn(s)");
    }

    private static async Task<PipelineMessage> RunBlockAsync(
        AgentBlock block,
        PipelineContext context
    )
    {
        var binding = block.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();
        await using var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            new PipelineMessage(context),
            context.RunId.ToString("N"),
            CancellationToken.None
        );

        PipelineMessage? output = null;
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
            else if (evt is WorkflowOutputEvent outputEvent && outputEvent.Is<PipelineMessage>())
            {
                output = outputEvent.As<PipelineMessage>();
            }
        }

        failure.Should().BeNull("the block workflow should not throw");
        return output ?? throw new InvalidOperationException("The block produced no output.");
    }

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
                "Tandem",
                "bin",
                "Debug",
                "net10.0",
                "Tandem"
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
