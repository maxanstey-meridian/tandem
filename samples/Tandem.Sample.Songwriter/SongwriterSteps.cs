namespace Tandem.Sample.Songwriter;

public sealed record SongwriterSteps(
    AgentDefinition<SongwriterState> Songwriter,
    LintStage Lint,
    AgentDefinition<SongwriterState> Proofreader,
    IPipelineNode<SongwriterState> Complete,
    IPipelineNode<SongwriterState> Failed
);

[PipelineStage(LintStage.StepId)]
public sealed partial class LintStage
{
    public const string StepId = "lint";

    public ValueTask<SongwriterState> ExecuteAsync(SongwriterState state, CancellationToken _) =>
        ValueTask.FromResult(SongwriterPolicies.Lint(state));
}
