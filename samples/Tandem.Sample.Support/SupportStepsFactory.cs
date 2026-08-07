using Microsoft.Extensions.AI;

namespace Tandem.Sample.Support;

public sealed class SupportStepsFactory(
    AgentRuntime agentRuntime,
    SupportOptions options,
    IAccountLookup accountLookup
)
{
    public SupportSteps Create(PipelineBuildContext context)
    {
        var classifier = agentRuntime
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
            .Build(context);
        var resolver = agentRuntime
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
            .Build(context);
        var customerReply = PipelineNodes.Request<SupportState, CustomerQuestion, CustomerReply>(
            SupportIds.AskCustomer,
            SupportIds.CustomerReply,
            SupportIds.ApplyReply,
            SupportPolicies.BuildCustomerQuestion,
            SupportPolicies.ApplyCustomerReply
        );

        return new SupportSteps(
            new ClassifyTicketAgent(classifier),
            new LoadAccountStage(accountLookup),
            new ResolveTicketAgent(resolver),
            customerReply,
            new CloseTicketStage(),
            new EscalateTicketStage(),
            PipelineNodes.Failed<SupportState>("support-failed")
        );
    }
}
