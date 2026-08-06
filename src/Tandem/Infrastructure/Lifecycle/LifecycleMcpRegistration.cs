using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Domain;
using Tandem.Infrastructure.Lifecycle.Validators;

namespace Tandem.Infrastructure.Lifecycle;

public static class LifecycleMcpRegistration
{
    public static IMcpServerBuilder AddLifecycleMcpTools(this IServiceCollection services)
    {
        var contracts = new[]
        {
            McpToolContractFactory.Create<AskPlannerRequest, AskPlannerRequestValidator>(
                "ask_planner"
            ),
            McpToolContractFactory.Create<SubmitReportRequest, SubmitReportRequestValidator>(
                "submit_report"
            ),
            McpToolContractFactory.Create<WriteCheckpointRequest, WriteCheckpointRequestValidator>(
                "write_checkpoint"
            ),
        };

        services.AddSingleton<IValidator<AskPlannerRequest>, AskPlannerRequestValidator>();
        services.AddSingleton<AskPlannerRequestValidator>();
        services.AddSingleton<IValidator<SubmitReportRequest>, SubmitReportRequestValidator>();
        services.AddSingleton<SubmitReportRequestValidator>();
        services.AddSingleton<
            IValidator<WriteCheckpointRequest>,
            WriteCheckpointRequestValidator
        >();
        services.AddSingleton<WriteCheckpointRequestValidator>();
        services.AddSingleton(new McpToolContractRegistry(contracts));
        services.AddSingleton<McpValidationFilter>();

        return services
            .AddMcpServer()
            .WithTools<LifecycleMcpTools>()
            .WithRequestFilters(filters =>
                filters.AddCallToolFilter(next =>
                    async (context, cancellationToken) =>
                    {
                        var filter = context.Services!.GetRequiredService<McpValidationFilter>();
                        return await filter.Create()(next)(context, cancellationToken);
                    }
                )
            );
    }
}
