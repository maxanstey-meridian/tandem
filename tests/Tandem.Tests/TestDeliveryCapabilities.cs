namespace Tandem.Tests;

internal static class TestDeliveryCapabilities
{
    public static (
        AgentCapability<DeliveryState> AskPlanner,
        AgentCapability<DeliveryState> SubmitReport,
        AgentCapability<DeliveryState> WriteCheckpoint
    ) Create()
    {
        return (
            AgentCapabilities.Create<DeliveryState, AskPlannerRequest>(
                "ask_planner",
                "Ask the planner.",
                new AskPlannerRequestValidator(),
                request => $"Planner asked: {request.Question}",
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.PlannerRequested(request),
                    }
            ),
            AgentCapabilities.Create<DeliveryState, SubmitReportRequest>(
                "submit_report",
                "Submit the implementation report.",
                new SubmitReportRequestValidator(),
                request => $"Report submitted: {request.Summary}",
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.ReportSubmitted(request),
                    }
            ),
            AgentCapabilities.Create<DeliveryState, WriteCheckpointRequest>(
                "write_checkpoint",
                "Write a checkpoint.",
                new WriteCheckpointRequestValidator(),
                request => $"Checkpoint written: {request.Summary}",
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.CheckpointWritten(request),
                    }
            )
        );
    }
}
