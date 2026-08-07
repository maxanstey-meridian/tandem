using Dunet;
using Tandem.Domain;

namespace Tandem.Sample.Debate;

public sealed record DebateSteps(
    OpenDebateStage Open,
    ProposerAgent Proposer,
    CriticAgent Critic,
    JudgeAgent Judge,
    CompleteDebateStage Complete
);

[PipelineStage("open")]
public sealed partial class OpenDebateStage
{
    [Union(EnableImplicitConversions = false)]
    public partial record OpenResult
    {
        public partial record Opened(DebateState State);
    }

    public ValueTask<OpenResult> ExecuteAsync(
        PipelineMessage<DebateState> pipeline,
        CancellationToken _
    ) => ValueTask.FromResult<OpenResult>(new OpenResult.Opened(pipeline.State));
}

[PipelineStage(ProposerAgent.StepId)]
public sealed partial class ProposerAgent(AgentOperation<DebateState> operation)
{
    public const string StepId = "proposer";

    [Union(EnableImplicitConversions = false)]
    public partial record ProposerResult
    {
        public partial record Proposed(
            DebateState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );
    }

    public async ValueTask<ProposerResult> ExecuteAsync(
        PipelineMessage<DebateState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return new ProposerResult.Proposed(result.State, result.Runtime, result.LatestOutcome!);
    }
}

[PipelineStage(CriticAgent.StepId)]
public sealed partial class CriticAgent(AgentOperation<DebateState> operation)
{
    public const string StepId = "critic";

    [Union(EnableImplicitConversions = false)]
    public partial record CriticResult
    {
        public partial record RevisionRequested(
            DebateState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );

        public partial record Accepted(
            DebateState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );
    }

    public async ValueTask<CriticResult> ExecuteAsync(
        PipelineMessage<DebateState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return result.LatestOutcome?.Kind == "debate.revision.requested"
            ? new CriticResult.RevisionRequested(result.State, result.Runtime, result.LatestOutcome)
            : new CriticResult.Accepted(result.State, result.Runtime, result.LatestOutcome!);
    }
}

[PipelineStage(JudgeAgent.StepId)]
public sealed partial class JudgeAgent(AgentOperation<DebateState> operation)
{
    public const string StepId = "judge";

    [Union(EnableImplicitConversions = false)]
    public partial record JudgeResult
    {
        public partial record VerdictSubmitted(
            DebateState State,
            PipelineRuntime Runtime,
            BlockOutcome Outcome
        );
    }

    public async ValueTask<JudgeResult> ExecuteAsync(
        PipelineMessage<DebateState> pipeline,
        CancellationToken cancellationToken
    )
    {
        var result = await operation.RunAsync(pipeline, cancellationToken);
        return new JudgeResult.VerdictSubmitted(
            result.State,
            result.Runtime,
            result.LatestOutcome!
        );
    }
}

[PipelineStage("complete")]
public sealed partial class CompleteDebateStage
{
    [Union(EnableImplicitConversions = false)]
    public partial record CompleteResult
    {
        public partial record Completed(DebateState State);
    }

    public ValueTask<CompleteResult> ExecuteAsync(
        PipelineMessage<DebateState> pipeline,
        CancellationToken _
    ) => ValueTask.FromResult<CompleteResult>(new CompleteResult.Completed(pipeline.State));
}
