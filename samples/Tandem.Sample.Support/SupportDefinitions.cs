namespace Tandem.Sample.Support;

public static class SupportDefinitions
{
    public static SupportSteps Create(
        AgentRuntime agentRuntime,
        SupportOptions options,
        IAccountLookup accountLookup
    )
    {
        var classifier = agentRuntime
            .Create<SupportState>(
                "support-classify",
                "support-classifier",
                SupportPrompts.Classifier,
                options.ClassifierClient
            )
            .WithMessage(SupportPrompts.ClassificationMessage)
            .WithOutput(new ClassificationDecisionValidator(), SupportPolicies.ApplyClassification)
            .WithSessionPolicy(SupportPolicies.StartClassificationFresh)
            .Build();
        var resolver = agentRuntime
            .Create<SupportState>(
                "support-resolve",
                "support-resolver",
                SupportPrompts.Resolver,
                options.ResolverClient
            )
            .WithMessage(SupportPrompts.ResolutionMessage)
            .WithOutput(new ResolutionDecisionValidator(), SupportPolicies.ApplyResolution)
            .WithSessionPolicy(SupportPolicies.StartResolutionFresh)
            .Build();
        var customerReply = PipelineNodes.WaitFor<SupportState, CustomerQuestion, CustomerReply>(
            SupportIds.CustomerReply,
            SupportPolicies.BuildCustomerQuestion,
            SupportPolicies.ApplyCustomerReply
        );

        return new SupportSteps(
            classifier,
            new LoadAccountStage(accountLookup),
            resolver,
            customerReply,
            PipelineNodes.Complete<SupportState>("support-close"),
            PipelineNodes.Complete<SupportState>("support-escalate"),
            PipelineNodes.Failed<SupportState>("support-failed")
        );
    }
}
