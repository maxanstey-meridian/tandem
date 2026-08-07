using Dunet;
using Tandem.Domain;

namespace Tandem.Delivery;

public sealed record DeliverySteps(
    PrepareWorkspaceStage PrepareWorkspace,
    ExecutorAgent Executor,
    PlannerAgent Planner,
    CaptureCandidateStage CaptureCandidate,
    VerificationStage Verification,
    ReviewerAgent Reviewer,
    CompleteRunStage CompleteRun,
    FailRunStage FailRun,
    HumanQuestionStage HumanQuestion,
    HumanInputPort HumanInput,
    ApplyHumanAnswerStage ApplyHumanAnswer
);

[PipelineStage(BlockIds.Prepare)]
public sealed partial class PrepareWorkspaceStage(PrepareWorkspaceBlock operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record PrepareWorkspaceResult
    {
        public partial record Prepared(DeliveryState State, BlockOutcome Outcome);

        public partial record Unexpected(DeliveryState State, BlockOutcome Outcome);
    }

    public async ValueTask<PrepareWorkspaceResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.ExecuteAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind == OutcomeKinds.WorkspacePrepared
            ? new PrepareWorkspaceResult.Prepared(result.State, result.LatestOutcome!)
            : new PrepareWorkspaceResult.Unexpected(result.State, result.LatestOutcome!);
    }
}

[PipelineStage(BlockIds.CaptureCandidate)]
public sealed partial class CaptureCandidateStage(CaptureCandidateBlock operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record CaptureCandidateResult
    {
        public partial record Captured(DeliveryState State, BlockOutcome Outcome);

        public partial record Unexpected(DeliveryState State, BlockOutcome Outcome);
    }

    public async ValueTask<CaptureCandidateResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.ExecuteAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind == OutcomeKinds.CandidateCaptured
            ? new CaptureCandidateResult.Captured(result.State, result.LatestOutcome!)
            : new CaptureCandidateResult.Unexpected(result.State, result.LatestOutcome!);
    }
}

[PipelineStage(BlockIds.Verify)]
public sealed partial class VerificationStage(VerificationBlock operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record VerificationStageResult
    {
        public partial record Passed(DeliveryState State, BlockOutcome Outcome);

        public partial record Failed(DeliveryState State, BlockOutcome Outcome);

        public partial record Unexpected(DeliveryState State, BlockOutcome Outcome);
    }

    public async ValueTask<VerificationStageResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.ExecuteAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind switch
        {
            OutcomeKinds.CommandPassed => new VerificationStageResult.Passed(
                result.State,
                result.LatestOutcome!
            ),
            OutcomeKinds.CommandFailed => new VerificationStageResult.Failed(
                result.State,
                result.LatestOutcome!
            ),
            _ => new VerificationStageResult.Unexpected(result.State, result.LatestOutcome!),
        };
    }
}

[PipelineStage(BlockIds.Complete)]
public sealed partial class CompleteRunStage(CompleteBlock operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record CompleteRunResult
    {
        public partial record Completed(DeliveryState State, BlockOutcome Outcome);
    }

    public async ValueTask<CompleteRunResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.ExecuteAsync(pipeline);
        return new CompleteRunResult.Completed(result.State, result.LatestOutcome!);
    }
}

[PipelineStage(BlockIds.Failed)]
public sealed partial class FailRunStage(FailedBlock operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record FailRunResult
    {
        public partial record Failed(DeliveryState State, BlockOutcome Outcome);
    }

    public async ValueTask<FailRunResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.ExecuteAsync(pipeline);
        return new FailRunResult.Failed(result.State, result.LatestOutcome!);
    }
}

[PipelineStage(BlockIds.Executor)]
public sealed partial class ExecutorAgent(AgentOperation<DeliveryState> operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record ExecutorResult
    {
        public partial record PlannerRequested(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record ReportSubmitted(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record CheckpointWritten(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record Unexpected(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );
    }

    public async ValueTask<ExecutorResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind switch
        {
            OutcomeKinds.PlannerRequested => new ExecutorResult.PlannerRequested(
                result.State,
                result.Runtime,
                result.LatestOutcome!
            ),
            OutcomeKinds.ReportSubmitted => new ExecutorResult.ReportSubmitted(
                result.State,
                result.Runtime,
                result.LatestOutcome!
            ),
            OutcomeKinds.CheckpointWritten => new ExecutorResult.CheckpointWritten(
                result.State,
                result.Runtime,
                result.LatestOutcome!
            ),
            _ => new ExecutorResult.Unexpected(result.State, result.Runtime, result.LatestOutcome!),
        };
    }
}

[PipelineStage(BlockIds.Planner)]
public sealed partial class PlannerAgent(AgentOperation<DeliveryState> operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record PlannerResult
    {
        public partial record Proceed(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record NeedsHuman(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record Stop(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record Unexpected(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );
    }

    public async ValueTask<PlannerResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind switch
        {
            OutcomeKinds.PlannerProceed or OutcomeKinds.PlannerProceedWithConstraints =>
                new PlannerResult.Proceed(result.State, result.Runtime, result.LatestOutcome!),
            OutcomeKinds.PlannerNeedsHuman => new PlannerResult.NeedsHuman(
                result.State,
                result.Runtime,
                result.LatestOutcome!
            ),
            OutcomeKinds.PlannerStop => new PlannerResult.Stop(
                result.State,
                result.Runtime,
                result.LatestOutcome!
            ),
            _ => new PlannerResult.Unexpected(result.State, result.Runtime, result.LatestOutcome!),
        };
    }
}

[PipelineStage(BlockIds.Reviewer)]
public sealed partial class ReviewerAgent(AgentOperation<DeliveryState> operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record ReviewerResult
    {
        public partial record Accepted(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record ChangesRequested(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record NeedsHuman(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record Unexpected(
            DeliveryState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );
    }

    public async ValueTask<ReviewerResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind switch
        {
            OutcomeKinds.ReviewAccepted => new ReviewerResult.Accepted(
                result.State,
                result.Runtime,
                result.LatestOutcome!
            ),
            OutcomeKinds.ReviewChangesRequested => new ReviewerResult.ChangesRequested(
                result.State,
                result.Runtime,
                result.LatestOutcome!
            ),
            OutcomeKinds.ReviewNeedsHuman => new ReviewerResult.NeedsHuman(
                result.State,
                result.Runtime,
                result.LatestOutcome!
            ),
            _ => new ReviewerResult.Unexpected(result.State, result.Runtime, result.LatestOutcome!),
        };
    }
}
