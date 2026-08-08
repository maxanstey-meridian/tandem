namespace Tandem.Sample.Support;

public static class SupportDefinitions
{
    public static SupportParticipants Create(SupportOptions options, IAccountLookup accountLookup)
    {
        var classifier = Agent
            .Create<SupportState>(
                "support-classify",
                SupportPrompts.Classifier,
                options.ClassifierClient
            )
            .WithMessage(SupportPrompts.ClassificationMessage)
            .WithOutput(new ClassificationDecisionValidator(), SupportPolicies.ApplyClassification)
            .Build();
        var resolver = Agent
            .Create<SupportState>(
                "support-resolve",
                SupportPrompts.Resolver,
                options.ResolverClient
            )
            .WithMessage(SupportPrompts.ResolutionMessage)
            .WithOutput(new ResolutionDecisionValidator(), SupportPolicies.ApplyResolution)
            .Build();
        var customerReply = PipelineNodes.WaitFor<SupportState, CustomerQuestion, CustomerReply>(
            SupportIds.CustomerReply,
            SupportPolicies.BuildCustomerQuestion,
            SupportPolicies.ApplyCustomerReply
        );

        return new SupportParticipants(
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
