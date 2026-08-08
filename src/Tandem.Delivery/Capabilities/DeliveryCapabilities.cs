using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class DeliveryCapabilities
{
    internal static DeliveryCapabilitySet Create(CheckpointAcceptance checkpointAcceptance)
    {
        var askPlanner = AgentCapabilities.Create<DeliveryState, AskPlannerRequest>(
            new AskPlannerCapability(),
            (state, request) => state.RecordPlannerRequest(request)
        );
        var submitReport = AgentCapabilities.Create<DeliveryState, SubmitReportRequest>(
            new SubmitReportCapability(),
            (state, request) => state.RecordImplementationReport(request)
        );
        var writeCheckpoint = AgentCapabilities
            .Create<DeliveryState, WriteCheckpointRequest>(
                new WriteCheckpointCapability(),
                (state, request) => state.RecordCheckpoint(request)
            )
            .WithAcceptance(
                (context, cancellationToken) =>
                    checkpointAcceptance.AcceptAsync(
                        $"{context.AcceptedCallId}--checkpoint",
                        context.State,
                        context.Request,
                        cancellationToken
                    )
            );
        return new DeliveryCapabilitySet(askPlanner, submitReport, writeCheckpoint);
    }
}

internal sealed record DeliveryCapabilitySet(
    AgentCapability<DeliveryState> AskPlanner,
    AgentCapability<DeliveryState> SubmitReport,
    AgentCapability<DeliveryState> WriteCheckpoint
);

internal sealed class AskPlannerCapability
    : IAgentCapabilityDefinition<DeliveryState, AskPlannerRequest>
{
    public string ToolName => "ask_planner";
    public string Instructions => "Ask the planner agent for guidance and end the current turn.";
    public FluentValidation.IValidator<AskPlannerRequest> Validator { get; } =
        new AskPlannerRequestValidator();

    public string Summarize(AskPlannerRequest request) => $"Planner asked: {request.Question}";
}

internal sealed class SubmitReportCapability
    : IAgentCapabilityDefinition<DeliveryState, SubmitReportRequest>
{
    public string ToolName => "submit_report";
    public string Instructions => "Submit the implementation report and end the current turn.";
    public FluentValidation.IValidator<SubmitReportRequest> Validator { get; } =
        new SubmitReportRequestValidator();

    public string Summarize(SubmitReportRequest request) => $"Report submitted: {request.Summary}";
}

internal sealed class WriteCheckpointCapability
    : IAgentCapabilityDefinition<DeliveryState, WriteCheckpointRequest>
{
    public string ToolName => "write_checkpoint";
    public string Instructions =>
        "Write a checkpoint of current work state and end the current turn.";
    public FluentValidation.IValidator<WriteCheckpointRequest> Validator { get; } =
        new WriteCheckpointRequestValidator();

    public string Summarize(WriteCheckpointRequest request) =>
        $"Checkpoint written: {request.Summary}";
}
