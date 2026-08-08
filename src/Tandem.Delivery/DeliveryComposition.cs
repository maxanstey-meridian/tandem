namespace Tandem.Delivery;

public sealed class DeliveryComposition
{
    private readonly DeliveryParticipants _delivery;

    public DeliveryComposition(DeliveryParticipantsFactory participantsFactory)
    {
        _delivery = participantsFactory.Create();
    }

    public PipelineInteraction<DeliveryState, HumanQuestion, HumanAnswer> HumanInput =>
        _delivery.HumanInput;

    public Pipeline<DeliveryState> Build()
    {
        var delivery = _delivery;
        return Pipeline
            .Start(
                at: delivery.PrepareWorkspace,
                name: "delivery",
                description: "Plan, implement, verify, and review a software change."
            )
            .Route(
                on: delivery.PrepareWorkspace.Success,
                to: delivery.Executor,
                label: "workspace prepared"
            )
            .Route(
                on: delivery.PrepareWorkspace.Failed,
                to: delivery.FailRun,
                label: "workspace failed"
            )
            .Route(
                on: delivery.Executor.Success,
                when: state => state.LastExecutorAction == ExecutorAction.PlannerRequested,
                to: delivery.Planner,
                label: "planner requested"
            )
            .Route(
                on: delivery.Executor.Success,
                when: state => state.LastExecutorAction == ExecutorAction.ReportSubmitted,
                to: delivery.CaptureCandidate,
                label: "report submitted"
            )
            .Route(
                on: delivery.Executor.Success,
                when: state => state.LastExecutorAction == ExecutorAction.CheckpointWritten,
                to: delivery.Executor,
                label: "checkpoint written"
            )
            .Route(on: delivery.Executor.Failed, to: delivery.FailRun, label: "agent failed")
            .Route(
                on: delivery.Planner.Success,
                when: IsPlannerProceed,
                to: delivery.Executor,
                label: "proceed / proceed with constraints"
            )
            .Route(
                on: delivery.Planner.Success,
                when: IsPlannerNeedsHuman,
                to: delivery.HumanInput,
                label: "needs human"
            )
            .Route(
                on: delivery.Planner.Success,
                when: IsPlannerStop,
                to: delivery.FailRun,
                label: "stop"
            )
            .Route(on: delivery.Planner.Failed, to: delivery.FailRun, label: "agent failed")
            .Route(
                on: delivery.CaptureCandidate.Success,
                when: HasVerificationCommands,
                to: delivery.Verification,
                label: "verification configured"
            )
            .Route(
                on: delivery.CaptureCandidate.Success,
                when: NoVerificationCommands,
                to: delivery.Reviewer,
                label: "no verification configured"
            )
            .Route(
                on: delivery.CaptureCandidate.Failed,
                to: delivery.FailRun,
                label: "capture failed"
            )
            .Route(
                on: delivery.Verification.Success,
                when: LatestCommandPassedAndCommandsRemain,
                to: delivery.Verification,
                label: "commands remain"
            )
            .Route(
                on: delivery.Verification.Success,
                when: LatestCommandPassedAndAllComplete,
                to: delivery.Reviewer,
                label: "verification complete"
            )
            .Route(
                on: delivery.Verification.Success,
                when: LatestCommandFailed,
                to: delivery.Executor,
                label: "command failed"
            )
            .Route(
                on: delivery.Verification.Failed,
                to: delivery.FailRun,
                label: "verification failed"
            )
            .Route(
                on: delivery.Reviewer.Success,
                when: IsReviewAccepted,
                to: delivery.CompleteRun,
                label: "accepted"
            )
            .Route(
                on: delivery.Reviewer.Success,
                when: IsReviewChangesRequested,
                to: delivery.Executor,
                label: "changes requested"
            )
            .Route(
                on: delivery.Reviewer.Success,
                when: IsReviewNeedsHuman,
                to: delivery.HumanInput,
                label: "needs human"
            )
            .Route(on: delivery.Reviewer.Failed, to: delivery.FailRun, label: "agent failed")
            .Route(
                when: IsPlannerHumanAnswer,
                from: delivery.HumanInput,
                to: delivery.Planner,
                label: "answer for planner"
            )
            .Route(
                when: IsReviewerHumanAnswer,
                from: delivery.HumanInput,
                to: delivery.Reviewer,
                label: "answer for reviewer"
            )
            .Route(
                when: IsUnknownHumanAnswer,
                from: delivery.HumanInput,
                to: delivery.FailRun,
                label: "unknown answer source"
            )
            .Build(delivery.CompleteRun, delivery.FailRun);
    }

    private static bool HasVerificationCommands(DeliveryState state) =>
        state.Packet.Verification.Count > 0;

    private static bool NoVerificationCommands(DeliveryState state) =>
        state.Packet.Verification.Count == 0;

    private static bool LatestCommandPassed(DeliveryState state) =>
        state.VerificationResults.LastOrDefault()?.ExitCode == 0;

    private static bool LatestCommandFailed(DeliveryState state) =>
        state.VerificationResults.LastOrDefault()?.ExitCode is not (null or 0);

    private static bool LatestCommandPassedAndCommandsRemain(DeliveryState state) =>
        LatestCommandPassed(state) && state.VerificationIndex < state.Packet.Verification.Count;

    private static bool LatestCommandPassedAndAllComplete(DeliveryState state) =>
        LatestCommandPassed(state) && state.VerificationIndex >= state.Packet.Verification.Count;

    private static bool IsPlannerProceed(DeliveryState state) =>
        state.PlannerDecision?.Decision
            is PlannerDecisionValue.Proceed
                or PlannerDecisionValue.ProceedWithConstraints;

    private static bool IsPlannerNeedsHuman(DeliveryState state) =>
        state.PlannerDecision?.Decision == PlannerDecisionValue.NeedsHuman;

    private static bool IsPlannerStop(DeliveryState state) =>
        state.PlannerDecision?.Decision == PlannerDecisionValue.Stop;

    private static bool IsReviewAccepted(DeliveryState state) =>
        state.ReviewerDecision?.Decision == ReviewDecisionValue.Accept;

    private static bool IsReviewChangesRequested(DeliveryState state) =>
        state.ReviewerDecision?.Decision == ReviewDecisionValue.RequestChanges;

    private static bool IsReviewNeedsHuman(DeliveryState state) =>
        state.ReviewerDecision?.Decision == ReviewDecisionValue.NeedsHuman;

    private static bool IsPlannerHumanAnswer(DeliveryState state) =>
        state.HumanAnswerSourceBlockId == BlockIds.Planner;

    private static bool IsReviewerHumanAnswer(DeliveryState state) =>
        state.HumanAnswerSourceBlockId == BlockIds.Reviewer;

    private static bool IsUnknownHumanAnswer(DeliveryState state) =>
        state.HumanAnswerSourceBlockId is not (BlockIds.Planner or BlockIds.Reviewer);
}
