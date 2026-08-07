using Dunet;
using Tandem.Domain;

namespace Tandem.Sample.Support;

public sealed record SupportSteps(
    ClassifyTicketAgent Classify,
    LoadAccountStage LoadAccount,
    ResolveTicketAgent Resolve,
    PipelineRequest<SupportState, CustomerQuestion, CustomerReply> CustomerReply,
    CloseTicketStage Close,
    EscalateTicketStage Escalate,
    IRawPipelineNode Failed
);

[PipelineStage(ClassifyTicketAgent.StepId)]
public sealed partial class ClassifyTicketAgent(AgentOperation<SupportState> operation)
{
    public const string StepId = "support-classify";

    public async ValueTask<Outcome<SupportState>> ExecuteAsync(
        SupportState state,
        CancellationToken cancellationToken
    )
    {
        return await operation.RunAsync(state, cancellationToken);
    }
}

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

[PipelineStage(ResolveTicketAgent.StepId)]
public sealed partial class ResolveTicketAgent(AgentOperation<SupportState> operation)
{
    public const string StepId = "support-resolve";

    [Union(EnableImplicitConversions = false)]
    public partial record ResolveResult
    {
        public partial record ResolutionProposed(SupportState State);

        public partial record Failed(SupportState State, FailureEvidence Failure);
    }

    public async ValueTask<ResolveResult> ExecuteAsync(
        SupportState state,
        CancellationToken cancellationToken
    )
    {
        return await operation.RunAsync<ResolveResult>(
            state,
            result => new ResolveResult.ResolutionProposed(result.State),
            failure => new ResolveResult.Failed(state, failure),
            cancellationToken
        );
    }
}

[PipelineStage(CloseTicketStage.StepId)]
public sealed partial class CloseTicketStage
{
    public const string StepId = "support-close";

    public ValueTask ExecuteAsync(SupportState _, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

[PipelineStage(EscalateTicketStage.StepId)]
public sealed partial class EscalateTicketStage
{
    public const string StepId = "support-escalate";

    public ValueTask ExecuteAsync(SupportState _, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
