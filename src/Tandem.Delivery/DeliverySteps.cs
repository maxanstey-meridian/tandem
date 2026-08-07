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
        public partial record Prepared(DeliveryState State);

        public partial record Unexpected(DeliveryState State);
    }

    public async ValueTask<PrepareWorkspaceResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        return await PipelineOperation.RunAsync<DeliveryState, PrepareWorkspaceResult>(
            () => operation.ExecuteAsync(pipeline, cancellationToken),
            result =>
                result.Outcome.Kind == OutcomeKinds.WorkspacePrepared
                    ? new PrepareWorkspaceResult.Prepared(result.State)
                    : new PrepareWorkspaceResult.Unexpected(result.State)
        );
    }
}

[PipelineStage(BlockIds.CaptureCandidate)]
public sealed partial class CaptureCandidateStage(CaptureCandidateBlock operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record CaptureCandidateResult
    {
        public partial record Captured(DeliveryState State);

        public partial record Unexpected(DeliveryState State);
    }

    public async ValueTask<CaptureCandidateResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        return await PipelineOperation.RunAsync<DeliveryState, CaptureCandidateResult>(
            () => operation.ExecuteAsync(pipeline, cancellationToken),
            result =>
                result.Outcome.Kind == OutcomeKinds.CandidateCaptured
                    ? new CaptureCandidateResult.Captured(result.State)
                    : new CaptureCandidateResult.Unexpected(result.State)
        );
    }
}

[PipelineStage(BlockIds.Verify)]
public sealed partial class VerificationStage(VerificationBlock operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record VerificationStageResult
    {
        public partial record Passed(DeliveryState State);

        public partial record Failed(DeliveryState State);

        public partial record Unexpected(DeliveryState State);
    }

    public async ValueTask<VerificationStageResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        return await PipelineOperation.RunAsync<DeliveryState, VerificationStageResult>(
            () => operation.ExecuteAsync(pipeline, cancellationToken),
            result =>
                result.Outcome.Kind switch
                {
                    OutcomeKinds.CommandPassed => new VerificationStageResult.Passed(result.State),
                    OutcomeKinds.CommandFailed => new VerificationStageResult.Failed(result.State),
                    _ => new VerificationStageResult.Unexpected(result.State),
                }
        );
    }
}

public sealed class CompleteRunStage(CompleteBlock operation) : IRawPipelineNode
{
    public string Id => BlockIds.Complete;

    public PipelineNodeDescriptor Descriptor { get; } =
        PipelineNodes.Stage<PipelineMessage<DeliveryState>, PipelineMessage<DeliveryState>>(
            BlockIds.Complete,
            (message, _, _) => operation.ExecuteAsync(message)
        );
}

[PipelineStage(BlockIds.Failed)]
public sealed partial class FailRunStage(FailedBlock operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record FailRunResult
    {
        public partial record Failed(DeliveryState State);
    }

    public async ValueTask<FailRunResult> ExecuteAsync(
        PipelineMessage<DeliveryState> pipeline,
        CancellationToken cancellationToken
    )
    {
        return await PipelineOperation.RunAsync<DeliveryState, FailRunResult>(
            () => operation.ExecuteAsync(pipeline),
            result => new FailRunResult.Failed(result.State)
        );
    }
}

[PipelineStage(BlockIds.Executor)]
public sealed partial class ExecutorAgent(AgentOperation<DeliveryState> operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record ExecutorResult
    {
        public partial record PlannerRequested(DeliveryState State);

        public partial record ReportSubmitted(DeliveryState State);

        public partial record CheckpointWritten(DeliveryState State);

        public partial record Unexpected(DeliveryState State);

        public partial record Failed(DeliveryState State, FailureEvidence Failure);
    }

    public async ValueTask<ExecutorResult> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    )
    {
        return await operation.RunAsync<ExecutorResult>(
            state,
            result =>
                result.Outcome.Kind switch
                {
                    OutcomeKinds.PlannerRequested => new ExecutorResult.PlannerRequested(
                        result.State
                    ),
                    OutcomeKinds.ReportSubmitted => new ExecutorResult.ReportSubmitted(
                        result.State
                    ),
                    OutcomeKinds.CheckpointWritten => new ExecutorResult.CheckpointWritten(
                        result.State
                    ),
                    _ => new ExecutorResult.Unexpected(result.State),
                },
            failure => new ExecutorResult.Failed(state, failure),
            cancellationToken
        );
    }
}

[PipelineStage(BlockIds.Planner)]
public sealed partial class PlannerAgent(AgentOperation<DeliveryState> operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record PlannerResult
    {
        public partial record Proceed(DeliveryState State);

        public partial record NeedsHuman(DeliveryState State);

        public partial record Stop(DeliveryState State);

        public partial record Unexpected(DeliveryState State);

        public partial record Failed(DeliveryState State, FailureEvidence Failure);
    }

    public async ValueTask<PlannerResult> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    )
    {
        return await operation.RunAsync<PlannerResult>(
            state,
            result =>
                result.Outcome.Kind switch
                {
                    OutcomeKinds.PlannerProceed or OutcomeKinds.PlannerProceedWithConstraints =>
                        new PlannerResult.Proceed(result.State),
                    OutcomeKinds.PlannerNeedsHuman => new PlannerResult.NeedsHuman(result.State),
                    OutcomeKinds.PlannerStop => new PlannerResult.Stop(result.State),
                    _ => new PlannerResult.Unexpected(result.State),
                },
            failure => new PlannerResult.Failed(state, failure),
            cancellationToken
        );
    }
}

[PipelineStage(BlockIds.Reviewer)]
public sealed partial class ReviewerAgent(AgentOperation<DeliveryState> operation)
{
    [Union(EnableImplicitConversions = false)]
    public partial record ReviewerResult
    {
        public partial record Accepted(DeliveryState State);

        public partial record ChangesRequested(DeliveryState State);

        public partial record NeedsHuman(DeliveryState State);

        public partial record Unexpected(DeliveryState State);

        public partial record Failed(DeliveryState State, FailureEvidence Failure);
    }

    public async ValueTask<ReviewerResult> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    )
    {
        return await operation.RunAsync<ReviewerResult>(
            state,
            result =>
                result.Outcome.Kind switch
                {
                    OutcomeKinds.ReviewAccepted => new ReviewerResult.Accepted(result.State),
                    OutcomeKinds.ReviewChangesRequested => new ReviewerResult.ChangesRequested(
                        result.State
                    ),
                    OutcomeKinds.ReviewNeedsHuman => new ReviewerResult.NeedsHuman(result.State),
                    _ => new ReviewerResult.Unexpected(result.State),
                },
            failure => new ReviewerResult.Failed(state, failure),
            cancellationToken
        );
    }
}
