using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Delivery;

public sealed class DeliveryComposition(DeliveryStepsFactory stepsFactory)
{
    public Pipeline Build(PipelineBuildContext context)
    {
        var delivery = stepsFactory.Create(context);
        return TandemWorkflow
            .Start(
                at: delivery.PrepareWorkspace,
                name: "delivery",
                description: "Plan, implement, verify, and review a software change."
            )
            .Route(
                on: delivery.PrepareWorkspace.Result.Prepared,
                to: delivery.Executor,
                label: "workspace prepared"
            )
            .Route(
                on: delivery.PrepareWorkspace.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.Executor.Result.PlannerRequested,
                to: delivery.Planner,
                label: "planner requested"
            )
            .Route(
                on: delivery.Executor.Result.ReportSubmitted,
                to: delivery.CaptureCandidate,
                label: "report submitted"
            )
            .Route(
                on: delivery.Executor.Result.CheckpointWritten,
                to: delivery.Executor,
                label: "checkpoint written"
            )
            .Route(
                on: delivery.Executor.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.Planner.Result.Proceed,
                to: delivery.Executor,
                label: "proceed / proceed with constraints"
            )
            .Route(
                on: delivery.Planner.Result.NeedsHuman,
                to: delivery.HumanQuestion,
                label: "needs human"
            )
            .Route(on: delivery.Planner.Result.Stop, to: delivery.FailRun, label: "stop")
            .Route(
                on: delivery.Planner.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.CaptureCandidate.Result.Captured,
                when: HasVerificationCommands,
                to: delivery.Verification,
                label: "verification configured"
            )
            .Route(
                on: delivery.CaptureCandidate.Result.Captured,
                when: NoVerificationCommands,
                to: delivery.Reviewer,
                label: "no verification configured"
            )
            .Route(
                on: delivery.CaptureCandidate.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.Verification.Result.Passed,
                when: HasRemainingCommands,
                to: delivery.Verification,
                label: "commands remain"
            )
            .Route(
                on: delivery.Verification.Result.Passed,
                when: AllCommandsComplete,
                to: delivery.Reviewer,
                label: "verification complete"
            )
            .Route(
                on: delivery.Verification.Result.Failed,
                to: delivery.Executor,
                label: "command failed"
            )
            .Route(
                on: delivery.Verification.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.Reviewer.Result.Accepted,
                to: delivery.CompleteRun,
                label: "accepted"
            )
            .Route(
                on: delivery.Reviewer.Result.ChangesRequested,
                to: delivery.Executor,
                label: "changes requested"
            )
            .Route(
                on: delivery.Reviewer.Result.NeedsHuman,
                to: delivery.HumanQuestion,
                label: "needs human"
            )
            .Route(
                on: delivery.Reviewer.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                from: delivery.HumanQuestion,
                to: delivery.HumanInput,
                label: "request human input"
            )
            .Route(
                from: delivery.HumanInput,
                to: delivery.ApplyHumanAnswer,
                label: "answer received"
            )
            .Route(
                when: IsPlannerHumanAnswer,
                from: delivery.ApplyHumanAnswer,
                to: delivery.Planner,
                label: "answer for planner"
            )
            .Route(
                when: IsReviewerHumanAnswer,
                from: delivery.ApplyHumanAnswer,
                to: delivery.Reviewer,
                label: "answer for reviewer"
            )
            .Route(
                when: IsUnknownHumanAnswer,
                from: delivery.ApplyHumanAnswer,
                to: delivery.FailRun,
                label: "unknown answer source"
            )
            .Build(delivery.CompleteRun, delivery.FailRun);
    }

    private static bool HasVerificationCommands(PipelineMessage<DeliveryState> msg) =>
        msg.State.Packet.Verification.Count > 0;

    private static bool NoVerificationCommands(PipelineMessage<DeliveryState> msg) =>
        msg.State.Packet.Verification.Count == 0;

    private static bool HasRemainingCommands(PipelineMessage<DeliveryState> msg) =>
        msg.State.VerificationIndex < msg.State.Packet.Verification.Count;

    private static bool AllCommandsComplete(PipelineMessage<DeliveryState> msg) =>
        msg.State.VerificationIndex >= msg.State.Packet.Verification.Count;

    private static bool IsPlannerHumanAnswer(PipelineMessage<DeliveryState> message) =>
        HumanAnswerSource(message) == BlockIds.Planner;

    private static bool IsReviewerHumanAnswer(PipelineMessage<DeliveryState> message) =>
        HumanAnswerSource(message) == BlockIds.Reviewer;

    private static bool IsUnknownHumanAnswer(PipelineMessage<DeliveryState> message) =>
        HumanAnswerSource(message) is not (BlockIds.Planner or BlockIds.Reviewer);

    private static string? HumanAnswerSource(PipelineMessage<DeliveryState> message) =>
        message.LatestOutcome?.Payload.TryGetProperty("sourceBlockId", out var source) == true
        && source.ValueKind == JsonValueKind.String
            ? source.GetString()
            : null;
}
