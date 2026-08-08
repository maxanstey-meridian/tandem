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
            .WithOutput(
                new ClassificationDecisionOutput(),
                (state, value) => state.RecordClassification(value)
            )
            .Build();
        var resolver = Agent
            .Create<SupportState>(
                "support-resolve",
                SupportPrompts.Resolver,
                options.ResolverClient
            )
            .WithMessage(SupportPrompts.ResolutionMessage)
            .WithOutput(
                new ResolutionDecisionOutput(),
                (state, value) => state.RecordResolution(value)
            )
            .Build();
        var customerReply = PipelineNodes.WaitFor<SupportState, CustomerQuestion, CustomerReply>(
            SupportIds.CustomerReply,
            state => state.CreateCustomerQuestion(),
            (state, reply) => state.RecordCustomerReply(reply)
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
