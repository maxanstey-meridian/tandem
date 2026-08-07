using Tandem.Advanced;
using Tandem.Domain;

namespace Tandem.Delivery;

public sealed record DeliverySteps(
    PrepareWorkspaceStage PrepareWorkspace,
    AgentDefinition<DeliveryState> Executor,
    AgentDefinition<DeliveryState> Planner,
    CaptureCandidateStage CaptureCandidate,
    VerificationStage Verification,
    AgentDefinition<DeliveryState> Reviewer,
    IPipelineNode<DeliveryState> CompleteRun,
    IPipelineNode<DeliveryState> FailRun,
    PipelineInteraction<DeliveryState, HumanQuestion, HumanAnswer> HumanInput
);

[PipelineStage(BlockIds.Prepare)]
public sealed partial class PrepareWorkspaceStage(PrepareWorkspaceBlock operation)
{
    public ValueTask<Outcome<DeliveryState>> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            pipeline => operation.ExecuteAsync(pipeline, cancellationToken),
            result => Expected(result, OutcomeKinds.WorkspacePrepared, BlockIds.Prepare)
        );

    private static Outcome<DeliveryState> Expected(
        OperationResult<DeliveryState> result,
        string expected,
        string blockId
    ) =>
        result.Outcome.Kind == expected
            ? new Outcome<DeliveryState>.Success(result.State)
            : Unexpected(result, blockId);

    internal static Outcome<DeliveryState>.Failed Unexpected(
        OperationResult<DeliveryState> result,
        string blockId
    ) =>
        new(
            result.State,
            new FailureEvidence(
                "delivery.unexpected_outcome",
                $"Block '{blockId}' produced unexpected outcome '{result.Outcome.Kind}'."
            )
        );
}

[PipelineStage(BlockIds.CaptureCandidate)]
public sealed partial class CaptureCandidateStage(CaptureCandidateBlock operation)
{
    public ValueTask<Outcome<DeliveryState>> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            pipeline => operation.ExecuteAsync(pipeline, cancellationToken),
            result =>
                result.Outcome.Kind == OutcomeKinds.CandidateCaptured
                    ? new Outcome<DeliveryState>.Success(result.State)
                    : PrepareWorkspaceStage.Unexpected(result, BlockIds.CaptureCandidate)
        );
}

[PipelineStage(BlockIds.Verify)]
public sealed partial class VerificationStage(VerificationBlock operation)
{
    public ValueTask<Outcome<DeliveryState>> ExecuteAsync(
        DeliveryState state,
        CancellationToken cancellationToken
    ) =>
        PipelineOperation.RunOutcomeAsync(
            state,
            pipeline => operation.ExecuteAsync(pipeline, cancellationToken),
            result =>
                result.Outcome.Kind is OutcomeKinds.CommandPassed or OutcomeKinds.CommandFailed
                    ? new Outcome<DeliveryState>.Success(result.State)
                    : PrepareWorkspaceStage.Unexpected(result, BlockIds.Verify)
        );
}
