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
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var executor = new ScriptedOutcomeBlock(
            BlockIds.Executor,
            new BlockOutcome(OutcomeKinds.ReportSubmitted, BlockIds.Executor, "report", default)
        );
        var planner = new ScriptedOutcomeBlock(BlockIds.Planner, OutcomeKinds.PlannerProceed);
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var reviewer = new ScriptedOutcomeBlock(BlockIds.Reviewer, OutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var plannerB = planner.BindExecutor();
        var captureB = capture.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();

        // Composition WITHOUT a planner edge: executor.report.submitted -> capture
        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-1-no-planner")
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = PipelineContext.Create(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        await CompositionRunner.RunAsync(workflow, new PipelineMessage(ctx), "proof-1");

        planner.InvocationCount.Should().Be(0, "planner must not run when no edge routes to it");
        executor.InvocationCount.Should().Be(1);
        capture.InvocationCount.Should().Be(1);
        reviewer.InvocationCount.Should().Be(1);
    }

    // Proof 2: Inserting a recording block before planner makes it run before planner.
    [Fact]
    public async Task InsertingRecordingBlockBeforePlanner_RunsBeforePlanner()
    {
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var executor = new ScriptedOutcomeBlock(BlockIds.Executor, OutcomeKinds.PlannerRequested);
        var recorder = new RecordingBlock("recorder", OutcomeKinds.PlannerRequested);
        var planner = new ScriptedOutcomeBlock(BlockIds.Planner, OutcomeKinds.PlannerProceed);
        var executor2 = new ScriptedOutcomeBlock("executor-2", OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var reviewer = new ScriptedOutcomeBlock(BlockIds.Reviewer, OutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);

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
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(
                executorB,
                recorderB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.PlannerRequested
            )
            .AddEdge(recorderB, plannerB)
            .AddEdge<PipelineMessage>(
                plannerB,
                executor2B,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.PlannerProceed
            )
            .AddEdge<PipelineMessage>(
                executor2B,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = PipelineContext.Create(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        await CompositionRunner.RunAsync(workflow, new PipelineMessage(ctx), "proof-2");

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
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var executor = new ScriptedOutcomeBlock(BlockIds.Executor, OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var verify = new ScriptedVerificationBlock(true, true);
        var reviewer = new ScriptedOutcomeBlock(BlockIds.Reviewer, OutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);

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

        var ctx = PipelineContext.Create(Guid.CreateVersion7(), packet, PinnedBase, Workspace);
        await CompositionRunner.RunAsync(workflow, new PipelineMessage(ctx), "proof-3");

        verify.InvokedIndices.Should().Equal(0, 1);
    }

    // Proof 4: A failed first command routes to executor and skips the second command.
    [Fact]
    public async Task FailedFirstCommand_RoutesToExecutor_SkipsSecondCommand()
    {
        var packet = TestPackets.MakePacket("cmd-1", "cmd-2");
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var executorInvocations = 0;
        var executor = new ScriptedOutcomeBlock(
            BlockIds.Executor,
            ctx =>
            {
                executorInvocations++;
                // First invocation: submit report. Second invocation: produce a terminal outcome to stop.
                return executorInvocations == 1
                    ? new BlockOutcome(
                        OutcomeKinds.ReportSubmitted,
                        BlockIds.Executor,
                        "report",
                        default
                    )
                    : new BlockOutcome(
                        "agent.completed",
                        BlockIds.Executor,
                        "done after remediation",
                        default
                    );
            }
        );
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var verify = new ScriptedVerificationBlock(false, true); // first fails, second would pass
        var reviewer = new ScriptedOutcomeBlock(BlockIds.Reviewer, OutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);
        var failed = new ScriptedOutcomeBlock(BlockIds.Failed, OutcomeKinds.RunFailed);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var captureB = capture.BindExecutor();
        var verifyB = verify.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();
        var failedB = failed.BindExecutor();

        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-4-failed-first-command")
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
                    && m!.Context.Packet.Verification.Count > 0
            )
            .AddEdge<PipelineMessage>(
                verifyB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.Context.VerificationIndex < m!.Context.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage>(
                verifyB,
                reviewerB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.Context.VerificationIndex >= m!.Context.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage>(
                verifyB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CommandFailed
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .AddEdge<PipelineMessage>(
                executorB,
                failedB,
                m =>
                    m!.LatestOutcome?.Kind != OutcomeKinds.ReportSubmitted
                    && m!.LatestOutcome?.Kind != OutcomeKinds.PlannerRequested
                    && m!.LatestOutcome?.Kind != OutcomeKinds.CheckpointWritten
            )
            .WithOutputFrom(completeB, failedB)
            .Build();

        var ctx = PipelineContext.Create(Guid.CreateVersion7(), packet, PinnedBase, Workspace);
        var output = await CompositionRunner.RunAsync(
            workflow,
            new PipelineMessage(ctx),
            "proof-4"
        );

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
            .Be(OutcomeKinds.RunFailed, "the second executor outcome has no route except failed");
    }

    // Proof 5: Passing both commands routes to reviewer.
    [Fact]
    public async Task PassingBothCommands_RoutesToReviewer()
    {
        var packet = TestPackets.MakePacket("cmd-1", "cmd-2");
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var executor = new ScriptedOutcomeBlock(BlockIds.Executor, OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var verify = new ScriptedVerificationBlock(true, true);
        var reviewer = new ScriptedOutcomeBlock(BlockIds.Reviewer, OutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);

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

        var ctx = PipelineContext.Create(Guid.CreateVersion7(), packet, PinnedBase, Workspace);
        await CompositionRunner.RunAsync(workflow, new PipelineMessage(ctx), "proof-5");

        verify.InvokedIndices.Should().Equal(0, 1);
        reviewer.InvocationCount.Should().Be(1, "reviewer must run after all commands pass");
        complete.InvocationCount.Should().Be(1);
    }

    // Proof 6: A composition without review completes after successful verification.
    [Fact]
    public async Task CompositionWithoutReview_CompletesAfterSuccessfulVerification()
    {
        var packet = TestPackets.MakePacket("cmd-1");
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var executor = new ScriptedOutcomeBlock(BlockIds.Executor, OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var verify = new ScriptedVerificationBlock(true);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var captureB = capture.BindExecutor();
        var verifyB = verify.BindExecutor();
        var completeB = complete.BindExecutor();

        // Composition WITHOUT review: verify passes -> complete
        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-6-no-review")
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
                    && m!.Context.Packet.Verification.Count > 0
            )
            .AddEdge<PipelineMessage>(
                verifyB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.Context.VerificationIndex < m!.Context.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage>(
                verifyB,
                completeB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.Context.VerificationIndex >= m!.Context.Packet.Verification.Count
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = PipelineContext.Create(Guid.CreateVersion7(), packet, PinnedBase, Workspace);
        var output = await CompositionRunner.RunAsync(
            workflow,
            new PipelineMessage(ctx),
            "proof-6"
        );

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.RunReady);
        complete.InvocationCount.Should().Be(1, "complete must run after verification passes");
    }

    // Proof 7: Two configured review blocks can run sequentially without runtime changes.
    [Fact]
    public async Task TwoReviewBlocks_RunSequentiallyWithoutRuntimeChanges()
    {
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var executor = new ScriptedOutcomeBlock(BlockIds.Executor, OutcomeKinds.ReportSubmitted);
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var reviewer1 = new ScriptedOutcomeBlock("reviewer-1", OutcomeKinds.ReviewChangesRequested);
        var reviewer2 = new ScriptedOutcomeBlock("reviewer-2", OutcomeKinds.ReviewAccepted);
        var executor2 = new ScriptedOutcomeBlock("executor-2", OutcomeKinds.ReportSubmitted);
        var capture2 = new ScriptedOutcomeBlock(
            "capture-candidate-2",
            OutcomeKinds.CandidateCaptured
        );
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);

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
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                reviewer1B,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                reviewer1B,
                executor2B,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewChangesRequested
            )
            .AddEdge<PipelineMessage>(
                executor2B,
                capture2B,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                capture2B,
                reviewer2B,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                reviewer2B,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = PipelineContext.Create(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        await CompositionRunner.RunAsync(workflow, new PipelineMessage(ctx), "proof-7");

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
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var executor = new ScriptedOutcomeBlock(
            BlockIds.Executor,
            ctx => new BlockOutcome(
                "report.submitted",
                BlockIds.Executor,
                "实现完成", // "implementation done" in Chinese
                JsonSerializer.SerializeToElement(new { })
            )
        );
        var secondAgent = new ScriptedOutcomeBlock("agent-zh", OutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);

        var prepareB = prepare.BindExecutor();
        var executorB = executor.BindExecutor();
        var secondAgentB = secondAgent.BindExecutor();
        var completeB = complete.BindExecutor();

        // Custom condition: route to agent-zh when summary contains Chinese characters
        static bool HasChinese(PipelineMessage? m) =>
            m!.LatestOutcome?.Summary != null
            && m.LatestOutcome.Summary.Any(c => c >= '\u4E00' && c <= '\u9FFF');

        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-8-chinese-routing")
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(executorB, secondAgentB, HasChinese)
            .AddEdge<PipelineMessage>(
                secondAgentB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        var ctx = PipelineContext.Create(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        await CompositionRunner.RunAsync(workflow, new PipelineMessage(ctx), "proof-8");

        secondAgent
            .InvocationCount.Should()
            .Be(1, "the Chinese-containing outcome must route to agent-zh");
        complete.InvocationCount.Should().Be(1);
    }

    // Proof 9: Usage below the configured threshold runs the normal executor invocation.
    [Fact]
    public async Task UsageBelowThreshold_RunsNormalExecutorInvocation()
    {
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);
        var normalExecutor = new ScriptedOutcomeBlock(
            BlockIds.Executor,
            ctx =>
            {
                // Normal invocation exposes ask_planner + submit_report
                var usage = ctx.AgentUsage.GetValueOrDefault(BlockIds.Executor);
                usage.Should().NotBeNull("executor usage must be present");
                var belowThreshold = usage!.CurrentContextTokens + 32000 < usage.CheckpointAtTokens;
                belowThreshold.Should().BeTrue("normal invocation only runs below threshold");
                return new BlockOutcome(
                    OutcomeKinds.ReportSubmitted,
                    BlockIds.Executor,
                    "normal",
                    default
                );
            }
        );
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var reviewer = new ScriptedOutcomeBlock(BlockIds.Reviewer, OutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);
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
        var ctx = PipelineContext.Create(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        ctx = ctx with
        {
            AgentUsage = new Dictionary<string, AgentUsage>
            {
                [BlockIds.Executor] = new AgentUsage(
                    CurrentInputTokens: 40000,
                    CurrentOutputTokens: 10000,
                    CurrentContextTokens: 50000,
                    ContextWindowTokens: 200000,
                    CheckpointAtTokens: 160000,
                    LastModelCallDuration: TimeSpan.Zero
                ),
            },
        };

        // Composition: normal route + checkpoint route
        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-9-below-threshold")
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(executorB, checkpointB, IsUsageAtOrAboveThreshold)
            .AddEdge<PipelineMessage>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        await CompositionRunner.RunAsync(workflow, new PipelineMessage(ctx), "proof-9");

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
        var prepare = new ScriptedOutcomeBlock(BlockIds.Prepare, OutcomeKinds.WorkspacePrepared);

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
            BlockIds.Executor,
            ctx =>
            {
                // After checkpoint: session must be cleared (no AgentSessions entry for executor)
                ctx.AgentSessions.ContainsKey(BlockIds.Executor)
                    .Should()
                    .BeFalse("executor session must be cleared after checkpoint");
                // The checkpoint payload should be available in context for the fresh prompt
                return new BlockOutcome(
                    OutcomeKinds.ReportSubmitted,
                    BlockIds.Executor,
                    "after-checkpoint",
                    default
                );
            }
        );
        var capture = new ScriptedOutcomeBlock(
            BlockIds.CaptureCandidate,
            OutcomeKinds.CandidateCaptured
        );
        var reviewer = new ScriptedOutcomeBlock(BlockIds.Reviewer, OutcomeKinds.ReviewAccepted);
        var complete = new ScriptedOutcomeBlock(BlockIds.Complete, OutcomeKinds.RunReady);

        var prepareB = prepare.BindExecutor();
        var checkpointB = checkpointExecutor.BindExecutor();
        var executorB = normalExecutor.BindExecutor();
        var captureB = capture.BindExecutor();
        var reviewerB = reviewer.BindExecutor();
        var completeB = complete.BindExecutor();

        // Context window 200000, checkpoint at 80% = 160000. Usage 140000 + 32000 >= 160000 → checkpoint
        var ctx = PipelineContext.Create(
            Guid.CreateVersion7(),
            TestPackets.MakePacket(),
            PinnedBase,
            Workspace
        );
        var checkpointPayload = JsonSerializer.SerializeToElement(
            new { summary = "prior work", next = new[] { "finish" } }
        );
        ctx = ctx with
        {
            AgentUsage = new Dictionary<string, AgentUsage>
            {
                [BlockIds.Executor] = new AgentUsage(
                    CurrentInputTokens: 100000,
                    CurrentOutputTokens: 40000,
                    CurrentContextTokens: 140000,
                    ContextWindowTokens: 200000,
                    CheckpointAtTokens: 160000,
                    LastModelCallDuration: TimeSpan.Zero
                ),
            },
            AgentSessions = new Dictionary<string, JsonElement>
            {
                [BlockIds.Executor] = JsonSerializer.SerializeToElement(
                    new { history = "old session" }
                ),
            },
        };

        var workflow = new WorkflowBuilder(prepareB)
            .WithName("proof-10-cross-threshold")
            .AddEdge<PipelineMessage>(
                prepareB,
                checkpointB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
                    && IsUsageAtOrAboveThreshold(m)
            )
            .AddEdge<PipelineMessage>(
                checkpointB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CheckpointWritten
            )
            .AddEdge<PipelineMessage>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        // Need to simulate session clearing between checkpoint and next executor.
        // The checkpoint block in the real AgentBlock clears the session. Here we
        // model it: the checkpoint block updates context to remove the session.
        // We use a custom block that does the session clearing.
        var clearingCheckpoint = new SessionClearingCheckpointBlock();
        var clearingCheckpointB = clearingCheckpoint.BindExecutor();

        var workflowWithClearing = new WorkflowBuilder(prepareB)
            .WithName("proof-10-cross-threshold-clearing")
            .AddEdge<PipelineMessage>(
                prepareB,
                clearingCheckpointB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
                    && IsUsageAtOrAboveThreshold(m)
            )
            .AddEdge<PipelineMessage>(
                clearingCheckpointB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CheckpointWritten
            )
            .AddEdge<PipelineMessage>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                reviewerB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .WithOutputFrom(completeB)
            .Build();

        await CompositionRunner.RunAsync(
            workflowWithClearing,
            new PipelineMessage(ctx),
            "proof-10"
        );

        clearingCheckpoint
            .InvocationCount.Should()
            .Be(1, "checkpoint-only mode must run when threshold crossed");
        normalExecutor.InvocationCount.Should().Be(1, "normal executor must run after checkpoint");
        complete.InvocationCount.Should().Be(1);
    }

    private static bool IsUsageAtOrAboveThreshold(PipelineMessage? m)
    {
        if (m is null)
        {
            return false;
        }

        var usage = m.Context.AgentUsage.GetValueOrDefault(BlockIds.Executor);
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
            .AddEdge<PipelineMessage>(
                prepareB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            )
            .AddEdge<PipelineMessage>(
                executorB,
                captureB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReportSubmitted
            )
            .AddEdge<PipelineMessage>(
                captureB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
                    && m!.Context.Packet.Verification.Count > 0
            )
            .AddEdge<PipelineMessage>(
                verifyB,
                verifyB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.Context.VerificationIndex < m!.Context.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage>(
                verifyB,
                reviewerB,
                m =>
                    m!.LatestOutcome?.Kind == OutcomeKinds.CommandPassed
                    && m!.Context.VerificationIndex >= m!.Context.Packet.Verification.Count
            )
            .AddEdge<PipelineMessage>(
                verifyB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.CommandFailed
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                completeB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewAccepted
            )
            .AddEdge<PipelineMessage>(
                reviewerB,
                executorB,
                m => m!.LatestOutcome?.Kind == OutcomeKinds.ReviewChangesRequested
            )
            .WithOutputFrom(completeB)
            .Build();
    }
}

/// <summary>
/// Checkpoint block that clears the executor session and records the checkpoint,
/// modeling the real AgentBlock's checkpoint-only behavior.
/// </summary>
internal sealed class SessionClearingCheckpointBlock : Executor<PipelineMessage, PipelineMessage>
{
    public int InvocationCount { get; private set; }

    public SessionClearingCheckpointBlock()
        : base("checkpoint-only") { }

    public override ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        InvocationCount++;
        var updatedCtx = message.Context.WithoutSession(BlockIds.Executor);
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new { summary = "checkpoint", next = new[] { "finish" } }
        );
        return ValueTask.FromResult(
            new PipelineMessage(
                updatedCtx,
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
