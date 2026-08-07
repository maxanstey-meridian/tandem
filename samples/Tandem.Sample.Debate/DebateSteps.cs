using Dunet;
using Tandem.Domain;

namespace Tandem.Sample.Debate;

public sealed record DebateSteps(
    OpenDebateStage Open,
    ProposerAgent Proposer,
    CriticAgent Critic,
    JudgeAgent Judge,
    CompleteDebateStage Complete,
    IRawPipelineNode Failed
);

[PipelineStage("open")]
public sealed partial class OpenDebateStage
{
    public ValueTask ExecuteAsync(DebateState _, CancellationToken __) => ValueTask.CompletedTask;
}

[PipelineStage(ProposerAgent.StepId)]
public sealed partial class ProposerAgent(AgentOperation<DebateState> operation)
{
    public const string StepId = "proposer";

    public async ValueTask<Outcome<DebateState>> ExecuteAsync(
        DebateState state,
        CancellationToken cancellationToken
    )
    {
        return await operation.RunAsync(state, cancellationToken);
    }
}

[PipelineStage(CriticAgent.StepId)]
public sealed partial class CriticAgent(AgentOperation<DebateState> operation)
{
    public const string StepId = "critic";

    [Union(EnableImplicitConversions = false)]
    public partial record CriticResult
    {
        public partial record RevisionRequested(DebateState State);

        public partial record Accepted(DebateState State);

        public partial record Failed(DebateState State, FailureEvidence Failure);
    }

    public async ValueTask<CriticResult> ExecuteAsync(
        DebateState state,
        CancellationToken cancellationToken
    )
    {
        return await operation.RunAsync<CriticResult>(
            state,
            result =>
                result.Outcome.Kind == "debate.revision.requested"
                    ? new CriticResult.RevisionRequested(result.State)
                    : new CriticResult.Accepted(result.State),
            failure => new CriticResult.Failed(state, failure),
            cancellationToken
        );
    }
}

[PipelineStage(JudgeAgent.StepId)]
public sealed partial class JudgeAgent(AgentOperation<DebateState> operation)
{
    public const string StepId = "judge";

    public async ValueTask<Outcome<DebateState>> ExecuteAsync(
        DebateState state,
        CancellationToken cancellationToken
    )
    {
        return await operation.RunAsync(state, cancellationToken);
    }
}

[PipelineStage("complete")]
public sealed partial class CompleteDebateStage
{
    public ValueTask ExecuteAsync(DebateState _, CancellationToken __) => ValueTask.CompletedTask;
}
