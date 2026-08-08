using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Tests.Composition;

/// <summary>
/// Composition proofs (plan 02 lines 948-970). These tests use MAF Workflows
/// and change only the WorkflowBuilder composition. Deterministic block
/// substitutes return prepared outcomes without invoking a model. They are
/// substitutes for block operations, not a fake workflow runtime.
/// </summary>
public sealed class CompositionProofTests
{
    private const string PinnedBase = "abc123";
    private const string Workspace = "/tmp/test-ws";

    // Proof 1: A composition without a planner edge never invokes the planner block.
    [Fact]
    public async Task CompositionWithoutPlannerEdge_NeverInvokesPlanner()
    {
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var executor = new ScriptedOutcomeBlock(
            DeliveryIds.Executor,
            new BlockOutcome(OutcomeKinds.ReportSubmitted, DeliveryIds.Executor, "report", default)
        );
        var planner = new ScriptedOutcomeBlock(
            DeliveryIds.Planner,
            ProofOutcomeKinds.PlannerProceed
        );
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var reviewer = new ScriptedOutcomeBlock(
            DeliveryIds.Reviewer,
            ProofOutcomeKinds.ReviewAccepted
        );
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var plannerB = planner.BindExecutor();
        var captureB = capture.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();

        // Composition WITHOUT a planner edge: executor.report.submitted -> capture
        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-1-no-planner")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = CreateMessage(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        await CompositionRunner.RunAsync(workflow, ctx, "proof-1");

        planner.InvocationCount.Should().Be(0, "planner must not run when no edge routes to it");
        executor.InvocationCount.Should().Be(1);
        capture.InvocationCount.Should().Be(1);
        reviewer.InvocationCount.Should().Be(1);
    }

    // Proof 2: Inserting a recording block before planner makes it run before planner.
    [Fact]
    public async Task InsertingRecordingBlockBeforePlanner_RunsBeforePlanner()
    {
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var executor = new ScriptedOutcomeBlock(
            DeliveryIds.Executor,
            OutcomeKinds.PlannerRequested
        );
        var recorder = new RecordingBlock("recorder", OutcomeKinds.PlannerRequested);
        var planner = new ScriptedOutcomeBlock(
            DeliveryIds.Planner,
            ProofOutcomeKinds.PlannerProceed
        );
        var executor2 = new ScriptedOutcomeBlock("executor-2", OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var reviewer = new ScriptedOutcomeBlock(
            DeliveryIds.Reviewer,
            ProofOutcomeKinds.ReviewAccepted
        );
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var recorderB = recorder.BindExecutor();
        var plannerB = planner.BindExecutor();
        var executor2B = executor2.BindExecutor();
        var captureB = capture.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();

        // Insert recorder between executor and planner
        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-2-recorder-before-planner")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                recorderB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.PlannerRequested
            )
            .AddEdge(recorderB, plannerB)
            .AddEdge<PipelineMessage<DeliveryState>>(
                plannerB,
                executor2B,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.PlannerProceed
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executor2B,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = CreateMessage(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        await CompositionRunner.RunAsync(workflow, ctx, "proof-2");

        recorder.InvocationCount.Should().Be(1, "recorder must run exactly once");
        planner.InvocationCount.Should().Be(1, "planner must run after recorder");
        recorder.ReceivedMessages.Should().HaveCount(1);
        recorder
            .ReceivedMessages.First()
            .LatestOutcome!.Kind.Should()
            .Be(OutcomeKinds.PlannerRequested);
    }

    // Proof 3: Two verification commands run in packet order.
    [Fact]
    public async Task TwoVerificationCommands_RunInPacketOrder()
    {
        var packet = TestPackets.MakePacket("cmd-1", "cmd-2");
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var executor = new ScriptedOutcomeBlock(DeliveryIds.Executor, OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var verify = new ScriptedVerificationOperation(true, true);
        var reviewer = new ScriptedOutcomeBlock(
            DeliveryIds.Reviewer,
            ProofOutcomeKinds.ReviewAccepted
        );
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var captureB = capture.BindExecutor();
        var verifyB = verify.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();

        var workflow = BuildWorkflowWithVerification(
            prepareB,
            executorB,
            captureB,
            verifyB,
            reviewerB,
            completeB,
            "proof-3"
        );

        var ctx = CreateMessage(Guid.CreateVersion7(), packet, PinnedBase, Workspace);
        await CompositionRunner.RunAsync(workflow, ctx, "proof-3");

        verify.InvokedIndices.Should().Equal(0, 1);
    }

    // Proof 4: A failed first command routes to executor and skips the second command.
    [Fact]
    public async Task FailedFirstCommand_RoutesToExecutor_SkipsSecondCommand()
    {
        var packet = TestPackets.MakePacket("cmd-1", "cmd-2");
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var executorInvocations = 0;
        var executor = new ScriptedOutcomeBlock(
            DeliveryIds.Executor,
            ctx =>
            {
                executorInvocations++;
                // First invocation: submit report. Second invocation: produce a terminal outcome to stop.
                return executorInvocations == 1
                    ? new BlockOutcome(
                        OutcomeKinds.ReportSubmitted,
                        DeliveryIds.Executor,
                        "report",
                        default
                    )
                    : new BlockOutcome(
                        "agent.completed",
                        DeliveryIds.Executor,
                        "done after remediation",
                        default
                    );
            }
        );
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var verify = new ScriptedVerificationOperation(false, true); // first fails, second would pass
        var reviewer = new ScriptedOutcomeBlock(
            DeliveryIds.Reviewer,
            ProofOutcomeKinds.ReviewAccepted
        );
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);
        var failed = new ScriptedOutcomeBlock(DeliveryIds.Failed, StandardOutcomeKinds.Failed);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var captureB = capture.BindExecutor();
        var verifyB = verify.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();
        var failedB = failed.BindExecutor();

        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-4-failed-first-command")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
                    && m!.State.Packet.Verification.Count > 0
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                verifyB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.State.VerificationIndex < m!.State.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                verifyB,
                reviewerB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.State.VerificationIndex >= m!.State.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                verifyB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CommandFailed
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                failedB,
                m =>
                    m!.LatestOutcome?.Kind != OutcomeKinds.ReportSubmitted
                    && m!.LatestOutcome?.Kind != OutcomeKinds.PlannerRequested
                    && m!.LatestOutcome?.Kind != OutcomeKinds.CheckpointWritten
            )
            .WithOutputFrom(completeB, failedB)
            .Build();

        var ctx = CreateMessage(Guid.CreateVersion7(), packet, PinnedBase, Workspace);
        var output = await CompositionRunner.RunAsync(workflow, ctx, "proof-4");

        verify
            .InvokedIndices.Should()
            .ContainSingle("only the first command should run")
            .Which.Should()
            .Be(0);
        executor
            .InvocationCount.Should()
            .Be(2, "executor runs once for report, once for remediation after failure");
        output
            .LatestOutcome!.Kind.Should()
            .Be(
                StandardOutcomeKinds.Failed,
                "the second executor outcome has no route except failed"
            );
    }

    // Proof 5: Passing both commands routes to reviewer.
    [Fact]
    public async Task PassingBothCommands_RoutesToReviewer()
    {
        var packet = TestPackets.MakePacket("cmd-1", "cmd-2");
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var executor = new ScriptedOutcomeBlock(DeliveryIds.Executor, OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var verify = new ScriptedVerificationOperation(true, true);
        var reviewer = new ScriptedOutcomeBlock(
            DeliveryIds.Reviewer,
            ProofOutcomeKinds.ReviewAccepted
        );
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var captureB = capture.BindExecutor();
        var verifyB = verify.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();

        var workflow = BuildWorkflowWithVerification(
            prepareB,
            executorB,
            captureB,
            verifyB,
            reviewerB,
            completeB,
            "proof-5"
        );

        var ctx = CreateMessage(Guid.CreateVersion7(), packet, PinnedBase, Workspace);
        await CompositionRunner.RunAsync(workflow, ctx, "proof-5");

        verify.InvokedIndices.Should().Equal(0, 1);
        reviewer.InvocationCount.Should().Be(1, "reviewer must run after all commands pass");
        complete.InvocationCount.Should().Be(1);
    }

    // Proof 6: A composition without review completes after successful verification.
    [Fact]
    public async Task CompositionWithoutReview_CompletesAfterSuccessfulVerification()
    {
        var packet = TestPackets.MakePacket("cmd-1");
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var executor = new ScriptedOutcomeBlock(DeliveryIds.Executor, OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var verify = new ScriptedVerificationOperation(true);
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var captureB = capture.BindExecutor();
        var verifyB = verify.BindExecutor();
        var completeB = complete.BindExecutor();

        // Composition WITHOUT review: verify passes -> complete
        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-6-no-review")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
                    && m!.State.Packet.Verification.Count > 0
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                verifyB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.State.VerificationIndex < m!.State.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                verifyB,
                completeB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.State.VerificationIndex >= m!.State.Packet.Verification.Count
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = CreateMessage(Guid.CreateVersion7(), packet, PinnedBase, Workspace);
        var output = await CompositionRunner.RunAsync(workflow, ctx, "proof-6");

        output.LatestOutcome!.Kind.Should().Be(StandardOutcomeKinds.Success);
        complete.InvocationCount.Should().Be(1, "complete must run after verification passes");
    }

    // Proof 7: Two configured review blocks can run sequentially without runtime changes.
    [Fact]
    public async Task TwoReviewBlocks_RunSequentiallyWithoutRuntimeChanges()
    {
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var executor = new ScriptedOutcomeBlock(DeliveryIds.Executor, OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var reviewer1 = new ScriptedOutcomeBlock(
            "reviewer-1",
            ProofOutcomeKinds.ReviewChangesRequested
        );
        var reviewer2 = new ScriptedOutcomeBlock("reviewer-2", ProofOutcomeKinds.ReviewAccepted);
        var executor2 = new ScriptedOutcomeBlock("executor-2", OutcomeKinds.ReportSubmitted);
        var capture2 = new ScriptedOutcomeBlock(
            "capture-candidate-2",
            StandardOutcomeKinds.Success
        );
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var captureB = capture.BindExecutor();
        var reviewer1B = reviewer1.BindExecutor();
        var reviewer2B = reviewer2.BindExecutor();
        var executor2B = executor2.BindExecutor();
        var capture2B = capture2.BindExecutor();
        var completeB = complete.BindExecutor();

        // reviewer-1 requests changes -> executor -> capture -> reviewer-2 accepts -> complete
        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-7-two-reviews")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                reviewer1B,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewer1B,
                executor2B,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewChangesRequested
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executor2B,
                capture2B,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                capture2B,
                reviewer2B,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewer2B,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = CreateMessage(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        await CompositionRunner.RunAsync(workflow, ctx, "proof-7");

        reviewer1.InvocationCount.Should().Be(1, "reviewer-1 must run");
        reviewer2
            .InvocationCount.Should()
            .Be(1, "reviewer-2 must run after reviewer-1 requests changes");
        complete.InvocationCount.Should().Be(1);
    }

    // Proof 8: A custom condition can route an executor outcome containing Chinese
    // characters to a second agent block configured with another model profile.
    [Fact]
    public async Task CustomCondition_RoutesChineseOutcomeToSecondAgentBlock()
    {
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var executor = new ScriptedOutcomeBlock(
            DeliveryIds.Executor,
            ctx => new BlockOutcome(
                "report.submitted",
                DeliveryIds.Executor,
                "实现完成", // "implementation done" in Chinese
                JsonSerializer.SerializeToElement(new { })
            )
        );
        var secondAgent = new ScriptedOutcomeBlock("agent-zh", ProofOutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var secondAgentB = secondAgent.BindExecutor();
        var completeB = complete.BindExecutor();

        // Custom condition: route to agent-zh when summary contains Chinese characters
        static bool HasChinese(PipelineMessage<DeliveryState>? m) =>
            m!.LatestOutcome?.Summary != null
            && m.LatestOutcome.Summary.Any(c => c >= '\u4E00' && c <= '\u9FFF');

        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-8-chinese-routing")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(executorB, secondAgentB, HasChinese)
            .AddEdge<PipelineMessage<DeliveryState>>(
                secondAgentB,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = CreateMessage(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        await CompositionRunner.RunAsync(workflow, ctx, "proof-8");

        secondAgent
            .InvocationCount.Should()
            .Be(1, "the Chinese-containing outcome must route to agent-zh");
        complete.InvocationCount.Should().Be(1);
    }

    // Proof 9: Usage below the configured threshold runs the normal executor invocation.
    [Fact]
    public async Task UsageBelowThreshold_RunsNormalExecutorInvocation()
    {
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);
        var normalExecutor = new ScriptedOutcomeBlock(
            DeliveryIds.Executor,
            ctx =>
            {
                // Normal invocation exposes ask_planner + submit_report
                return new BlockOutcome(
                    OutcomeKinds.ReportSubmitted,
                    DeliveryIds.Executor,
                    "normal",
                    default
                );
            }
        );
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var reviewer = new ScriptedOutcomeBlock(
            DeliveryIds.Reviewer,
            ProofOutcomeKinds.ReviewAccepted
        );
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);
        var checkpointExecutor = new ScriptedOutcomeBlock(
            "checkpoint-only",
            OutcomeKinds.CheckpointWritten
        );

        var prepareB = prepare.BindExecutor();
        var executorB = normalExecutor.BindExecutor();
        var captureB = capture.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();
        var checkpointB = checkpointExecutor.BindExecutor();

        // Context window 200000, checkpoint at 80% = 160000. Usage 50000 + 32000 < 160000 → normal
        var ctx = CreateMessage(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        ctx = ctx with
        {
            Runtime = ctx.Runtime.WithUsage(
                DeliveryIds.Executor,
                new AgentUsage(40000, 10000, 50000, 200000, 160000, TimeSpan.Zero)
            ),
        };

        // Composition: normal route + checkpoint route
        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-9-below-threshold")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                checkpointB,
                IsUsageAtOrAboveThreshold
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        await CompositionRunner.RunAsync(workflow, ctx, "proof-9");

        normalExecutor.InvocationCount.Should().Be(1, "normal executor must run below threshold");
        checkpointExecutor
            .InvocationCount.Should()
            .Be(0, "checkpoint mode must not run below threshold");
    }

    // Proof 10: Usage crossing the threshold runs checkpoint-only mode, accepts one typed
    // checkpoint, clears the old session, and starts the next executor invocation with that checkpoint.
    [Fact]
    public async Task UsageCrossingThreshold_RunsCheckpointOnlyMode_ThenFreshSession()
    {
        var prepare = new ScriptedOutcomeBlock(DeliveryIds.Prepare, StandardOutcomeKinds.Success);

        var checkpointExecutor = new ScriptedOutcomeBlock(
            "checkpoint-only",
            ctx =>
            {
                // Checkpoint-only mode: write_checkpoint exposed, returns checkpoint.written
                return new BlockOutcome(
                    OutcomeKinds.CheckpointWritten,
                    "checkpoint-only",
                    "checkpoint",
                    JsonSerializer.SerializeToElement(
                        new { summary = "work so far", next = new[] { "finish" } }
                    )
                );
            }
        );

        var normalExecutor = new ScriptedOutcomeBlock(
            DeliveryIds.Executor,
            ctx =>
            {
                // After checkpoint: session must be cleared (no AgentSessions entry for executor)
                // The checkpoint payload should be available in context for the fresh prompt
                return new BlockOutcome(
                    OutcomeKinds.ReportSubmitted,
                    DeliveryIds.Executor,
                    "after-checkpoint",
                    default
                );
            }
        );
        var capture = new ScriptedOutcomeBlock(
            DeliveryIds.CaptureCandidate,
            StandardOutcomeKinds.Success
        );
        var reviewer = new ScriptedOutcomeBlock(
            DeliveryIds.Reviewer,
            ProofOutcomeKinds.ReviewAccepted
        );
        var complete = new ScriptedOutcomeBlock(DeliveryIds.Complete, StandardOutcomeKinds.Success);

        var prepareB = prepare.BindExecutor();
        var checkpointB = checkpointExecutor.BindExecutor();
        var executorB = normalExecutor.BindExecutor();
        var captureB = capture.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();

        // Context window 200000, checkpoint at 80% = 160000. Usage 140000 + 32000 >= 160000 → checkpoint
        var ctx = CreateMessage(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        var checkpoint = new WriteCheckpointRequest(
            "prior work",
            ["inspected"],
            ["README.md"],
            [],
            "finish"
        );
        ctx = ctx with
        {
            Runtime = ctx
                .Runtime.WithUsage(
                    DeliveryIds.Executor,
                    new AgentUsage(100000, 40000, 140000, 200000, 160000, TimeSpan.Zero)
                )
                .WithSession(
                    DeliveryIds.Executor,
                    JsonSerializer.SerializeToElement(new { history = "old session" })
                ),
            State = ctx.State with
            {
                ExecutorTransition = new ExecutorTransition.CheckpointWritten(checkpoint),
            },
        };

        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-10-cross-threshold")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                checkpointB,
                m =>
                    m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
                    && IsUsageAtOrAboveThreshold(m)
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                checkpointB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CheckpointWritten
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        // Need to simulate session clearing between checkpoint and next executor.
        // The checkpoint block in the real AgentBlock<DeliveryState> clears the session. Here we
        // model it: the checkpoint block updates context to remove the session.
        // We use a custom block that does the session clearing.
        var clearingCheckpoint = new SessionClearingCheckpointBlock();
        var clearingCheckpointB = clearingCheckpoint.BindExecutor();

        var workflowWithClearing = new WorkflowBuilder(prepareB)
            .WithName("proof-10-cross-threshold-clearing")
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                clearingCheckpointB,
                m =>
                    m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
                    && IsUsageAtOrAboveThreshold(m)
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                clearingCheckpointB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CheckpointWritten
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        await CompositionRunner.RunAsync(workflowWithClearing, ctx, "proof-10");

        clearingCheckpoint
            .InvocationCount.Should()
            .Be(1, "checkpoint-only mode must run when threshold crossed");
        normalExecutor.InvocationCount.Should().Be(1, "normal executor must run after checkpoint");
        complete.InvocationCount.Should().Be(1);
    }

    private static bool IsUsageAtOrAboveThreshold(PipelineMessage<DeliveryState>? m)
    {
        if (m is null)
        {
            return false;
        }

        var usage = m.Runtime.AgentUsage.GetValueOrDefault(DeliveryIds.Executor);
        if (usage is null)
        {
            return false;
        }

        return usage.CurrentContextTokens + 32000 >= usage.CheckpointAtTokens;
    }

    private static Workflow BuildWorkflowWithVerification(
        ExecutorBinding prepareB,
        ExecutorBinding executorB,
        ExecutorBinding captureB,
        ExecutorBinding verifyB,
        ExecutorBinding reviewerB,
        ExecutorBinding completeB,
        string name
    )
    {
        return new WorkflowBuilder(prepareB)
            .WithName(name)
            .AddEdge<PipelineMessage<DeliveryState>>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                captureB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == StandardOutcomeKinds.Success
                    && m!.State.Packet.Verification.Count > 0
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                verifyB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.State.VerificationIndex < m!.State.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                verifyB,
                reviewerB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.State.VerificationIndex >= m!.State.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                verifyB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CommandFailed
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewAccepted
            )
            .AddEdge<PipelineMessage<DeliveryState>>(
                reviewerB,
                executorB,
                m => m!.LatestOutcome?.Kind == ProofOutcomeKinds.ReviewChangesRequested
            )
            .WithOutputFrom(completeB)
            .Build();
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
}

/// <summary>
/// Checkpoint block that clears the executor session and records the checkpoint,
/// modeling the real generic agent block's checkpoint-only behavior.
/// </summary>
internal sealed class SessionClearingCheckpointBlock
    : Executor<PipelineMessage<DeliveryState>, PipelineMessage<DeliveryState>>
{
    public int InvocationCount { get; private set; }

    public SessionClearingCheckpointBlock()
        : base("checkpoint-only") { }

    public override ValueTask<PipelineMessage<DeliveryState>> HandleAsync(
        PipelineMessage<DeliveryState> message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        InvocationCount++;
        var updatedRuntime = message.Runtime.WithoutSession(DeliveryIds.Executor);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new { summary = "checkpoint", next = new[] { "finish" } }
        );
        return ValueTask.FromResult(
            new PipelineMessage<DeliveryState>(
                updatedRuntime,
                message.State,
                new BlockOutcome(
                    OutcomeKinds.CheckpointWritten,
                    "checkpoint-only",
                    "checkpoint",
                    payload
                )
            )
        );
    }
}

internal static class ProofOutcomeKinds
{
    internal const string PlannerProceed = "proof.planner.proceed";
    internal const string ReviewAccepted = "proof.review.accepted";
    internal const string ReviewChangesRequested = "proof.review.changes_requested";
}
