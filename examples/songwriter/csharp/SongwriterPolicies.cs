using FluentValidation;
using Tandem;

namespace Examples.Songwriter;

public static class SongwriterPolicies
{
    public static SongwriterState Lint(SongwriterState state) =>
        state with
        {
            LintFeedback =
                state.Lyrics?.Contains('\n') == true
                    ? null
                    : "Lyrics must contain more than one line.",
        };
}

public sealed class SongDecisionValidator : AbstractValidator<SongDecision>
{
    public SongDecisionValidator()
    {
        RuleFor(decision => decision.Lyrics).NotEmpty();
    }
}

public sealed class SongDecisionOutput : IAgentOutputDefinition<SongwriterState, SongDecision>
{
    public string Instructions => "Return the complete revised song lyrics.";
    public IValidator<SongDecision> Validator { get; } = new SongDecisionValidator();

    public IReadOnlyList<AgentOutputExample<SongDecision>> Examples(SongwriterState state) =>
        [new(state.Brief, new SongDecision("First line\nSecond line"))];
}

public sealed class ProofreaderDecisionValidator : AbstractValidator<ProofreaderDecision>
{
    public ProofreaderDecisionValidator()
    {
        RuleFor(decision => decision.Feedback).NotEmpty();
    }
}

public sealed class ProofreaderDecisionOutput
    : IAgentOutputDefinition<SongwriterState, ProofreaderDecision>
{
    public string Instructions => "Return the proofread decision and actionable feedback.";
    public IValidator<ProofreaderDecision> Validator { get; } = new ProofreaderDecisionValidator();

    public IReadOnlyList<AgentOutputExample<ProofreaderDecision>> Examples(SongwriterState state) =>
        [
            new(
                state.Lyrics ?? state.Brief,
                new ProofreaderDecision(true, "The lyrics satisfy the brief.")
            ),
        ];
}
