using Dunet;
using Tandem.Domain;

namespace Tandem.Sample.Support;

public sealed record SupportSteps(
    ClassifyTicketAgent Classify,
    LoadAccountStage LoadAccount,
    ResolveTicketAgent Resolve,
    PipelineRequest<SupportState, CustomerQuestion, CustomerReply> CustomerReply,
    CloseTicketStage Close,
    EscalateTicketStage Escalate
);

[PipelineStage(ClassifyTicketAgent.StepId)]
public sealed partial class ClassifyTicketAgent(AgentOperation<SupportState> operation)
{
    public const string StepId = "support-classify";

    [Union(EnableImplicitConversions = false)]
    public partial record ClassifyResult
    {
        public partial record Categorized(
            SupportState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record Unexpected(
            SupportState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );
    }

    public async ValueTask<ClassifyResult> ExecuteAsync(
        PipelineMessage<SupportState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind == SupportPolicies.CategorizedOutcome
            ? new ClassifyResult.Categorized(result.State, result.Runtime, result.LatestOutcome)
            : new ClassifyResult.Unexpected(result.State, result.Runtime, result.LatestOutcome!);
    }
}

[PipelineStage(LoadAccountStage.StepId)]
public sealed partial class LoadAccountStage(IAccountLookup accountLookup)
{
    public const string StepId = "support-load-account";

    [Union(EnableImplicitConversions = false)]
    public partial record LoadAccountResult
    {
        public partial record Loaded(SupportState State);
    }

    public async ValueTask<LoadAccountResult> ExecuteAsync(
        PipelineMessage<SupportState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var context = await accountLookup.LoadAsync(pipeline.State, cancellationToken);
        return new LoadAccountResult.Loaded(pipeline.State with { AccountContext = context });
    }
}

[PipelineStage(ResolveTicketAgent.StepId)]
public sealed partial class ResolveTicketAgent(AgentOperation<SupportState> operation)
{
    public const string StepId = "support-resolve";

    [Union(EnableImplicitConversions = false)]
    public partial record ResolveResult
    {
        public partial record ResolutionProposed(
            SupportState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record Unexpected(
            SupportState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );
    }

    public async ValueTask<ResolveResult> ExecuteAsync(
        PipelineMessage<SupportState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind == SupportPolicies.ResolutionProposedOutcome
            ? new ResolveResult.ResolutionProposed(
                result.State,
                result.Runtime,
                result.LatestOutcome
            )
            : new ResolveResult.Unexpected(result.State, result.Runtime, result.LatestOutcome!);
    }
}

[PipelineStage(CloseTicketStage.StepId)]
public sealed partial class CloseTicketStage
{
    public const string StepId = "support-close";

    [Union(EnableImplicitConversions = false)]
    public partial record CloseResult
    {
        public partial record Closed(SupportState State);
    }

    public ValueTask<CloseResult> ExecuteAsync(
        PipelineMessage<SupportState> pipeline,
        CancellationToken _
    ) => ValueTask.FromResult<CloseResult>(new CloseResult.Closed(pipeline.State));
}

[PipelineStage(EscalateTicketStage.StepId)]
public sealed partial class EscalateTicketStage
{
    public const string StepId = "support-escalate";

    [Union(EnableImplicitConversions = false)]
    public partial record EscalateResult
    {
        public partial record Escalated(SupportState State);
    }

    public ValueTask<EscalateResult> ExecuteAsync(
        PipelineMessage<SupportState> pipeline,
        CancellationToken _
    ) => ValueTask.FromResult<EscalateResult>(new EscalateResult.Escalated(pipeline.State));
}
