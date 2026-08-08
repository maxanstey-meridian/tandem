namespace Tandem.Tests;

internal sealed class TestCapabilityDefinition<TState, TRequest>(
    string toolName,
    string instructions,
    FluentValidation.IValidator<TRequest> validator,
    Func<TRequest, string> summarize
) : IAgentCapabilityDefinition<TState, TRequest>
    where TRequest : class
{
    public string ToolName => toolName;
    public string Instructions => instructions;
    public FluentValidation.IValidator<TRequest> Validator => validator;

    public string Summarize(TRequest request) => summarize(request);
}

internal sealed class TestOutputDefinition<TState, TOutput>(
    string instructions,
    FluentValidation.IValidator<TOutput> validator
) : IAgentOutputDefinition<TState, TOutput>
{
    public string Instructions => instructions;
    public FluentValidation.IValidator<TOutput> Validator => validator;
}

internal static class TestDeliveryCapabilities
{
    public static (
        AgentCapability<DeliveryState> AskPlanner,
        AgentCapability<DeliveryState> SubmitReport,
        AgentCapability<DeliveryState> WriteCheckpoint
    ) Create()
    {
        return (
            AgentCapabilities.Create(
                new TestCapabilityDefinition<DeliveryState, AskPlannerRequest>(
                    "ask_planner",
                    "Ask the planner.",
                    new AskPlannerRequestValidator(),
                    request => $"Planner asked: {request.Question}"
                ),
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.PlannerRequested(request),
                    }
            ),
            AgentCapabilities.Create(
                new TestCapabilityDefinition<DeliveryState, SubmitReportRequest>(
                    "submit_report",
                    "Submit the implementation report.",
                    new SubmitReportRequestValidator(),
                    request => $"Report submitted: {request.Summary}"
                ),
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.ReportSubmitted(request),
                    }
            ),
            AgentCapabilities.Create(
                new TestCapabilityDefinition<DeliveryState, WriteCheckpointRequest>(
                    "write_checkpoint",
                    "Write a checkpoint.",
                    new WriteCheckpointRequestValidator(),
                    request => $"Checkpoint written: {request.Summary}"
                ),
                (state, request) =>
                    state with
                    {
                        ExecutorTransition = new ExecutorTransition.CheckpointWritten(request),
                    }
            )
        );
    }
}
