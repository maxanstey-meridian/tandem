using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Tandem.Sample.Support;

public sealed record SupportOptions(IChatClient ClassifierClient, IChatClient ResolverClient);

public static class SupportRegistration
{
    public static IServiceCollection AddCustomerSupport(
        this IServiceCollection services,
        SupportOptions options
    )
    {
        services.AddSingleton(options);
        services.AddSingleton<ClassifyTicketAgent>(sp =>
            new(CreateClassifier(sp.GetRequiredService<AgentRuntime>(), options))
        );
        services.AddSingleton<LoadAccountStage>();
        services.AddSingleton<ResolveTicketAgent>(sp =>
            new(CreateResolver(sp.GetRequiredService<AgentRuntime>(), options))
        );
        services.AddSingleton(_ =>
            PipelineNodes.Request<SupportState, CustomerQuestion, CustomerReply>(
                SupportIds.AskCustomer,
                SupportIds.CustomerReply,
                SupportIds.ApplyReply,
                SupportPolicies.BuildCustomerQuestion,
                SupportPolicies.ApplyCustomerReply
            )
        );
        services.AddSingleton<CloseTicketStage>();
        services.AddSingleton<EscalateTicketStage>();
        services.AddSingleton<SupportSteps>();
        services.AddSingleton<SupportComposition>();
        return services;
    }

    private static AgentOperation<SupportState> CreateClassifier(
        AgentRuntime runtime,
        SupportOptions options
    ) =>
        runtime
            .Create<SupportState>(
                ClassifyTicketAgent.StepId,
                "support-classifier",
                SupportPrompts.Classifier,
                options.ClassifierClient
            )
            .WithMessage(SupportPrompts.ClassificationMessage)
            .WithStructuredOutput(
                SupportPolicies.ParseClassification,
                chat =>
                    chat.ResponseFormat = ChatResponseFormat.ForJsonSchema<ClassificationDecision>()
            )
            .WithSessionPolicy(SupportPolicies.StartClassificationFresh)
            .Build();

    private static AgentOperation<SupportState> CreateResolver(
        AgentRuntime runtime,
        SupportOptions options
    ) =>
        runtime
            .Create<SupportState>(
                ResolveTicketAgent.StepId,
                "support-resolver",
                SupportPrompts.Resolver,
                options.ResolverClient
            )
            .WithMessage(SupportPrompts.ResolutionMessage)
            .WithStructuredOutput(
                SupportPolicies.ParseResolution,
                chat => chat.ResponseFormat = ChatResponseFormat.ForJsonSchema<ResolutionDecision>()
            )
            .WithSessionPolicy(SupportPolicies.StartResolutionFresh)
            .Build();
}
