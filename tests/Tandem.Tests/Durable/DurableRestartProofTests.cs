using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;
using Tandem.Domain;

namespace Tandem.Tests.Durable;

/// <summary>
/// Durable restart proof (plan 02 lines 1002-1014). Start simple-v1, let prepare
/// and executor complete, stop the host before the next block completes, restart
/// with the same workflow definition and stable block IDs, verify the existing
/// durable run continues and already completed blocks are not repeated, verify
/// it reaches the expected terminal output.
/// </summary>
[Collection("Durable Task Scheduler")]
public sealed class DurableRestartProofTests
{
    [Fact]
    public async Task RestartedHost_ResumesSimpleV1_WithoutRepeatingCompletedBlocks()
    {
        DtsFixture.EnsureReachable();

        using var runDirectory = new TemporaryDirectory();
        var invocationLogPath = Path.Combine(runDirectory.Path, "invocations.txt");

        var workflow = BuildSimpleV1DeterministicWorkflow(
            invocationLogPath,
            "durable-restart-proof"
        );
        var runId = "durable-restart-" + Guid.NewGuid().ToString("N");

        // Start the run in the first host. Let prepare and executor complete,
        // then stop the host before planner completes (it blocks forever on first call).
        var packet = new Packet(
            Title: "Test packet",
            Repository: "file:///nonexistent",
            Base: "main",
            Outcomes: [new Outcome("o1", "Do the thing.")],
            Verification: [],
            Constraints: [],
            ImplementationContext: ""
        );
        var initialMessage = new PipelineMessage(
            PipelineContext.Create(Guid.CreateVersion7(), packet, "abc123", "/tmp/test-ws")
        );

        await using (
            var firstHost = await DurableHost.StartAsync(options => options.AddWorkflow(workflow))
        )
        {
            await firstHost.WorkflowClient.RunAsync(workflow, initialMessage, runId);

            // Wait for prepare, executor, and planner to be recorded (planner blocks forever).
            await WaitForInvocationsAsync(invocationLogPath, 3);
        }

        // Restart with the same workflow definition and stable block IDs.
        var restartedWorkflow = BuildSimpleV1DeterministicWorkflow(
            invocationLogPath,
            "durable-restart-proof"
        );

        await using var restartedHost = await DurableHost.StartAsync(options =>
            options.AddWorkflow(restartedWorkflow)
        );

        var existingRun = await restartedHost.DurableTaskClient.GetInstanceAsync(runId);
        existingRun.Should().NotBeNull("the durable run must survive host shutdown");

        // The planner was blocking forever. We need to resume it by delivering
        // the planner decision through the durable custom status / event mechanism.
        // For this deterministic proof, we use a workflow where the second planner
        // invocation returns a decision instead of blocking.

        var completed = await restartedHost.DurableTaskClient.WaitForInstanceCompletionAsync(
            runId,
            getInputsAndOutputs: true,
            CancellationToken.None
        );

        completed.Should().NotBeNull();
        completed!.RuntimeStatus.Should().Be(OrchestrationRuntimeStatus.Completed);

        // Verify the completed blocks (prepare, executor) were not repeated after restart.
        var invocations = File.ReadAllLines(invocationLogPath);
        var prepareCount = invocations.Count(l => l == "prepare");
        var executorCount = invocations.Count(l => l == "executor");
        var completeCount = invocations.Count(l => l == "complete");
        prepareCount.Should().Be(1, "prepare must not be repeated after restart");
        executorCount.Should().Be(1, "executor must not be repeated after restart");
        completeCount.Should().Be(1, "complete must run exactly once");
    }

    /// <summary>
    /// Builds a simple-v1-shaped deterministic workflow. The planner blocks forever
    /// on its first invocation (to allow host shutdown mid-run), then returns a
    /// proceed decision on subsequent invocations.
    /// </summary>
    private static Workflow BuildSimpleV1DeterministicWorkflow(
        string invocationLogPath,
        string workflowName
    )
    {
        var prepare = new LoggingBlock(
            invocationLogPath,
            BlockIds.Prepare,
            OutcomeKinds.WorkspacePrepared
        );
        var executor = new LoggingBlock(
            invocationLogPath,
            BlockIds.Executor,
            OutcomeKinds.PlannerRequested
        );
        var planner = new BlockingPlannerBlock(invocationLogPath);
        var executor2 = new LoggingBlock(
            invocationLogPath,
            "executor-2",
            OutcomeKinds.ReportSubmitted
        );
        var capture = new LoggingBlock(
            invocationLogPath,
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var reviewer = new LoggingBlock(
            invocationLogPath,
            BlockIds.Reviewer,
            OutcomeKinds.ReviewAccepted
        );
        var complete = new LoggingBlock(
            invocationLogPath,
            BlockIds.Complete,
            OutcomeKinds.RunReady
        );
        var failed = new LoggingBlock(invocationLogPath, BlockIds.Failed, OutcomeKinds.RunFailed);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var plannerB = planner.BindExecutor();
        var executor2B = executor2.BindExecutor();
        var captureB = capture.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();
        var failedB = failed.BindExecutor();

        return new WorkflowBuilder(prepareB)
            .WithName(workflowName)
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(
                prepareB,
                failedB,
                m => m!.LatestOutcome?.Kind != OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(
                executorB,
                plannerB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.PlannerRequested
            )
            .AddEdge<PipelineMessage>(
                executorB,
                failedB,
                m =>
                    m!.LatestOutcome?.Kind != OutcomeKinds.PlannerRequested
                    && m!.LatestOutcome?.Kind != OutcomeKinds.ReportSubmitted
                    && m!.LatestOutcome?.Kind != OutcomeKinds.CheckpointWritten
            )
            .AddEdge<PipelineMessage>(
                plannerB,
                executor2B,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.PlannerProceed
            )
            .AddEdge<PipelineMessage>(
                plannerB,
                failedB,
                m => m!.LatestOutcome?.Kind != OutcomeKinds.PlannerProceed
            )
            .AddEdge<PipelineMessage>(
                executor2B,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                executor2B,
                failedB,
                m => m!.LatestOutcome?.Kind != OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                captureB,
                failedB,
                m => m!.LatestOutcome?.Kind != OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                failedB,
                m => m!.LatestOutcome?.Kind != OutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB, failedB)
            .Build();
    }

    private static async Task WaitForInvocationsAsync(string path, int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (File.Exists(path))
            {
                var count = File.ReadAllLines(path).Length;
                if (count >= expectedCount)
                {
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private sealed class LoggingBlock : Executor<PipelineMessage, PipelineMessage>
    {
        private readonly string _logPath;
        private readonly string _outcomeKind;

        public LoggingBlock(string logPath, string blockId, string outcomeKind)
            : base(blockId)
        {
            _logPath = logPath;
            _outcomeKind = outcomeKind;
        }

        public override ValueTask<PipelineMessage> HandleAsync(
            PipelineMessage message,
            IWorkflowContext context,
            CancellationToken cancellationToken
        )
        {
            File.AppendAllText(_logPath, Id + "\n");
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { });
            return ValueTask.FromResult(
                new PipelineMessage(
                    message.Context,
                    new BlockOutcome(_outcomeKind, Id, _outcomeKind, payload)
                )
            );
        }
    }

    /// <summary>
    /// Planner block that blocks forever on its first invocation (to allow host
    /// shutdown mid-run), then returns planner.proceed on subsequent invocations.
    /// The transition is controlled by a file marker.
    /// </summary>
    private sealed class BlockingPlannerBlock(string logPath)
        : Executor<PipelineMessage, PipelineMessage>(BlockIds.Planner)
    {
        public override async ValueTask<PipelineMessage> HandleAsync(
            PipelineMessage message,
            IWorkflowContext context,
            CancellationToken cancellationToken
        )
        {
            File.AppendAllText(logPath, "planner\n");

            // Block forever on first invocation — the host shutdown will cancel it.
            // On subsequent invocations (after restart), return proceed.
            var markerPath = Path.Combine(Path.GetDirectoryName(logPath)!, "planner-proceeded.txt");
            if (!File.Exists(markerPath))
            {
                File.WriteAllText(markerPath, "proceeded");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new PipelineMessage(
                message.Context,
                new BlockOutcome(
                    OutcomeKinds.PlannerProceed,
                    BlockIds.Planner,
                    "proceed",
                    System.Text.Json.JsonSerializer.SerializeToElement(new { })
                )
            );
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-durable-restart-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for a test fixture.
            }
        }
    }
}
