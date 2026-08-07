namespace Tandem.Sample.Support;

public sealed class SupportComposition(SupportStepsFactory stepsFactory)
{
    public Pipeline Build(PipelineBuildContext context)
    {
        var support = stepsFactory.Create(context);
        return TandemWorkflow
            .Start(
                at: support.Classify,
                name: "customer-support",
                description: "Classify, resolve, and confirm a customer support ticket."
            )
            .Route(
                on: support.Classify.Result.Success,
                to: support.LoadAccount,
                label: "classified"
            )
            .Route(on: support.LoadAccount, to: support.Resolve, label: "account loaded")
            .Route(
                on: support.Resolve.Result.ResolutionProposed,
                to: support.CustomerReply.Request,
                label: "resolution proposed"
            )
            .Route(on: support.Resolve.Result.Failed, to: support.Failed, label: "agent failed")
            .Route(
                from: support.CustomerReply.Request,
                to: support.CustomerReply.Port,
                label: "wait for customer"
            )
            .Route(
                from: support.CustomerReply.Port,
                to: support.CustomerReply.Resume,
                label: "customer replied"
            )
            .Route(
                when: state => state.FinalDisposition == "closed",
                from: support.CustomerReply.Resume,
                to: support.Close,
                label: "customer confirmed"
            )
            .Route(
                when: state => state.FinalDisposition == "escalated",
                from: support.CustomerReply.Resume,
                to: support.Escalate,
                label: "customer still blocked"
            )
            .Build(support.Close, support.Escalate, support.Failed);
    }
}
