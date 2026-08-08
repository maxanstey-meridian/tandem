using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class DeliveryCapabilities
{
    internal static DeliveryCapabilitySet Create(
        IDeliveryRecordSink records,
        CheckpointAcceptance checkpointAcceptance
    )
    {
        var askPlanner = AgentCapabilities
            .Create<DeliveryState, AskPlannerRequest>(
                "ask_planner",
                "Ask the planner agent for guidance and end the current turn.",
                new AskPlannerRequestValidator(),
                request => $"Planner asked: {request.Question}",
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.PlannerRequested(request),
                    }
            )
            .WithAcceptance(
                (context, cancellationToken) =>
                    records.AcceptCapabilityAsync(
                        context.AcceptedCallId,
                        "ask_planner",
                        context.Request,
                        cancellationToken
                    )
            );
        var submitReport = AgentCapabilities
            .Create<DeliveryState, SubmitReportRequest>(
                "submit_report",
                "Submit the implementation report and end the current turn.",
                new SubmitReportRequestValidator(),
                request => $"Report submitted: {request.Summary}",
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.ReportSubmitted(request),
                    }
            )
            .WithAcceptance(
                async (context, cancellationToken) =>
                {
                    await records.AcceptReportAsync(
                        context.AcceptedCallId,
                        context.Request,
                        cancellationToken
                    );
                    await records.AcceptCapabilityAsync(
                        context.AcceptedCallId,
                        "submit_report",
                        context.Request,
                        cancellationToken
                    );
                }
            );
        var writeCheckpoint = AgentCapabilities
            .Create<DeliveryState, WriteCheckpointRequest>(
                "write_checkpoint",
                "Write a checkpoint of current work state and end the current turn.",
                new WriteCheckpointRequestValidator(),
                request => $"Checkpoint written: {request.Summary}",
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.CheckpointWritten(request),
                    }
            )
            .WithAcceptance(
                async (context, cancellationToken) =>
                {
                    await checkpointAcceptance.AcceptAsync(
                        $"{context.AcceptedCallId}--checkpoint",
                        context.State,
                        context.Request,
                        cancellationToken
                    );
                    await records.AcceptCapabilityAsync(
                        context.AcceptedCallId,
                        "write_checkpoint",
                        context.Request,
                        cancellationToken
                    );
                }
            );
        return new DeliveryCapabilitySet(askPlanner, submitReport, writeCheckpoint);
    }
}

internal sealed record DeliveryCapabilitySet(
    AgentCapability<DeliveryState> AskPlanner,
    AgentCapability<DeliveryState> SubmitReport,
    AgentCapability<DeliveryState> WriteCheckpoint
);
