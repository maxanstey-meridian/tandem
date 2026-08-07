using Dunet;
using Tandem.Domain;

namespace Tandem.Sample.Songwriter;

public sealed record SongwriterSteps(
    SongwriterAgent Songwriter,
    LintStage Lint,
    ProofreaderAgent Proofreader,
    CompleteSongStage Complete,
    IRawPipelineNode Failed
);

[PipelineStage(SongwriterAgent.StepId)]
public sealed partial class SongwriterAgent(AgentOperation<SongwriterState> operation)
{
    public const string StepId = "songwriter";

    public async ValueTask<Outcome<SongwriterState>> ExecuteAsync(
        SongwriterState state,
        CancellationToken cancellationToken
    ) => await operation.RunAsync(state, cancellationToken);
}

[PipelineStage(LintStage.StepId)]
public sealed partial class LintStage
{
    public const string StepId = "lint";

    [Union(EnableImplicitConversions = false)]
    public partial record LintResult
    {
        public partial record Passed(SongwriterState State);

        public partial record Failed(SongwriterState State);
    }

    public ValueTask<LintResult> ExecuteAsync(SongwriterState state, CancellationToken _)
    {
        state = SongwriterPolicies.Lint(state);
        return ValueTask.FromResult<LintResult>(
            state.LintFeedback is null ? new LintResult.Passed(state) : new LintResult.Failed(state)
        );
    }
}

[PipelineStage(ProofreaderAgent.StepId)]
public sealed partial class ProofreaderAgent(AgentOperation<SongwriterState> operation)
{
    public const string StepId = "proofreader";

    [Union(EnableImplicitConversions = false)]
    public partial record ProofreaderResult
    {
        public partial record Accepted(SongwriterState State);

        public partial record ChangesRequested(SongwriterState State);

        public partial record Failed(SongwriterState State, FailureEvidence Failure);
    }

    public async ValueTask<ProofreaderResult> ExecuteAsync(
        SongwriterState state,
        CancellationToken cancellationToken
    ) =>
        await operation.RunAsync<ProofreaderResult>(
            state,
            result =>
                result.Outcome.Kind == SongwriterPolicies.ProofAcceptedOutcome
                    ? new ProofreaderResult.Accepted(result.State)
                    : new ProofreaderResult.ChangesRequested(result.State),
            failure => new ProofreaderResult.Failed(state, failure),
            cancellationToken
        );
}

[PipelineStage(CompleteSongStage.StepId)]
public sealed partial class CompleteSongStage
{
    public const string StepId = "complete";

    public ValueTask ExecuteAsync(SongwriterState _, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
