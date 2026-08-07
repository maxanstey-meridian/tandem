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
                (state, _) => state with { LastExecutorAction = ExecutorAction.PlannerRequested }
            ),
            AgentCapabilities.Create<DeliveryState, SubmitReportRequest>(
                "submit_report",
                "Submit the implementation report.",
                new SubmitReportRequestValidator(),
                request => $"Report submitted: {request.Summary}",
                (state, request) =>
                    state with
                    {
                        ImplementationReport = System.Text.Json.JsonSerializer.SerializeToElement(
                            request,
                            System.Text.Json.JsonSerializerOptions.Web
                        ),
                        LastExecutorAction = ExecutorAction.ReportSubmitted,
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
                        CheckpointPayload = System.Text.Json.JsonSerializer.SerializeToElement(
                            request,
                            System.Text.Json.JsonSerializerOptions.Web
                        ),
                        LastExecutorAction = ExecutorAction.CheckpointWritten,
                    }
            )
        );
    }
}
