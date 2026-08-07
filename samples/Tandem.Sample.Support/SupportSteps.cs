namespace Tandem.Sample.Support;

public sealed record SupportSteps(
    AgentDefinition<SupportState> Classify,
    LoadAccountStage LoadAccount,
    AgentDefinition<SupportState> Resolve,
    PipelineInteraction<SupportState, CustomerQuestion, CustomerReply> CustomerReply,
    IPipelineNode<SupportState> Close,
    IPipelineNode<SupportState> Escalate,
    IPipelineNode<SupportState> Failed
);

[PipelineStage(LoadAccountStage.StepId)]
public sealed partial class LoadAccountStage(IAccountLookup accountLookup)
{
    public const string StepId = "support-load-account";

    public async ValueTask<SupportState> ExecuteAsync(
        SupportState state,
        CancellationToken cancellationToken
    )
    {
        var context = await accountLookup.LoadAsync(state, cancellationToken);
        return state with { AccountContext = context };
    }
}
