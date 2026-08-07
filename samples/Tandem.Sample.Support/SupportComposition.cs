namespace Tandem.Sample.Support;

public sealed class SupportComposition(SupportSteps support)
{
    public Pipeline Build() =>
        TandemWorkflow
            .Start(
                at: support.Classify,
                name: "customer-support",
                description: "Classify, resolve, and confirm a customer support ticket."
            )
            .Route(
                on: support.Classify.Result.Categorized,
                to: support.LoadAccount,
                label: "classified"
            )
            .Route(
                on: support.LoadAccount.Result.Loaded,
                to: support.Resolve,
                label: "account loaded"
            )
            .Route(
                on: support.Resolve.Result.ResolutionProposed,
                to: support.CustomerReply.Request,
                label: "resolution proposed"
            )
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
                when: message => message.State.FinalDisposition == "closed",
                from: support.CustomerReply.Resume,
                to: support.Close,
                label: "customer confirmed"
            )
            .Route(
                when: message => message.State.FinalDisposition == "escalated",
                from: support.CustomerReply.Resume,
                to: support.Escalate,
                label: "customer still blocked"
            )
            .Build(support.Close, support.Escalate);
}
