namespace Tandem.Sample.Support;

public sealed class SupportComposition(SupportSteps support)
{
    public Pipeline Build()
    {
        return TandemWorkflow
            .Start(
                at: support.Classify,
                name: "customer-support",
                description: "Classify, resolve, and confirm a customer support ticket."
            )
            .Route(on: support.Classify.Success, to: support.LoadAccount, label: "classified")
            .Route(on: support.LoadAccount, to: support.Resolve, label: "account loaded")
            .Route(
                on: support.Resolve.Success,
                to: support.CustomerReply,
                label: "resolution proposed"
            )
            .Route(on: support.Resolve.Failed, to: support.Failed, label: "agent failed")
            .Route(
                when: state => state.FinalDisposition == "closed",
                from: support.CustomerReply,
                to: support.Close,
                label: "customer confirmed"
            )
            .Route(
                when: state => state.FinalDisposition == "escalated",
                from: support.CustomerReply,
                to: support.Escalate,
                label: "customer still blocked"
            )
            .Build(support.Close, support.Escalate, support.Failed);
    }
}
