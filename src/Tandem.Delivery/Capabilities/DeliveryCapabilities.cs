namespace Tandem.Delivery;

internal static class DeliveryCapabilities
{
    internal static DeliveryCapabilitySet Create()
    {
        var askPlanner = AgentCapabilities.Create<DeliveryState, AskPlannerRequest>(
            "ask_planner",
            "Ask the planner block for guidance and end the current turn.",
            new AskPlannerRequestValidator(),
            request => $"Planner asked: {request.Question}",
            (state, request) =>
                state with
                {
                    ExecutorAcceptedFact = new ExecutorAcceptedFact.PlannerRequested(request),
                }
        );
        var submitReport = AgentCapabilities.Create<DeliveryState, SubmitReportRequest>(
            "submit_report",
            "Submit the implementation report and end the current turn.",
            new SubmitReportRequestValidator(),
            request => $"Report submitted: {request.Summary}",
            (state, request) =>
                state with
                {
                    ExecutorAcceptedFact = new ExecutorAcceptedFact.ReportSubmitted(request),
                }
        );
        var writeCheckpoint = AgentCapabilities.Create<DeliveryState, WriteCheckpointRequest>(
            "write_checkpoint",
            "Write a checkpoint of current work state and end the current turn.",
            new WriteCheckpointRequestValidator(),
            request => $"Checkpoint written: {request.Summary}",
            (state, request) =>
                state with
                {
                    ExecutorAcceptedFact = new ExecutorAcceptedFact.CheckpointWritten(request),
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
