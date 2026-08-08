using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed record DeliveryAgentProfile(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent
);

public sealed record DeliveryOptions(
    Func<string, IChatClient> ChatClients,
    Func<string, DeliveryAgentProfile> Profiles
);

public static class DeliveryRegistration
{
    public static IServiceCollection AddDelivery(
        this IServiceCollection services,
        DeliveryOptions options
    )
    {
        var capabilities = CreateCapabilities();
        var askPlanner = capabilities.AskPlanner;
        var submitReport = capabilities.SubmitReport;
        var writeCheckpoint = capabilities.WriteCheckpoint;
        services.AddSingleton(askPlanner);
        services.AddSingleton(submitReport);
        services.AddSingleton(writeCheckpoint);
        services.AddSingleton<GitProcess>();
        services.AddSingleton<WorkspacePreparation>();
        services.AddSingleton<DeliveryDiffAcquisition>();
        services.AddSingleton<DeliveryStepsFactory>(sp =>
        {
            return new DeliveryStepsFactory(
                sp.GetRequiredService<AgentFactory>(),
                options.ChatClients,
                options.Profiles,
                sp.GetRequiredService<DeliveryDiffAcquisition>(),
                sp.GetRequiredService<WorkspacePreparation>(),
                sp.GetRequiredService<GitProcess>(),
                askPlanner,
                submitReport,
                writeCheckpoint
            );
        });
        services.AddSingleton<DeliveryComposition>();
        return services;
    }

    internal static DeliveryCapabilitySet CreateCapabilities()
    {
        var askPlanner = AgentCapabilities.Create<DeliveryState, AskPlannerRequest>(
            "ask_planner",
            "Ask the planner block for guidance and end the current turn.",
            new AskPlannerRequestValidator(),
            request => $"Planner asked: {request.Question}",
            (state, _) => state with { LastExecutorAction = ExecutorAction.PlannerRequested }
        );
        var submitReport = AgentCapabilities.Create<DeliveryState, SubmitReportRequest>(
            "submit_report",
            "Submit the implementation report and end the current turn.",
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
        );
        var writeCheckpoint = AgentCapabilities.Create<DeliveryState, WriteCheckpointRequest>(
            "write_checkpoint",
            "Write a checkpoint of current work state and end the current turn.",
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
        );
        return new DeliveryCapabilitySet(askPlanner, submitReport, writeCheckpoint);
    }
}

internal sealed record DeliveryCapabilitySet(
    AgentCapability<DeliveryState> AskPlanner,
    AgentCapability<DeliveryState> SubmitReport,
    AgentCapability<DeliveryState> WriteCheckpoint
);
