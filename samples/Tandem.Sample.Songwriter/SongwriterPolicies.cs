using FluentValidation;

namespace Tandem.Sample.Songwriter;

public static class SongwriterPolicies
{
    public static SongwriterState ApplySong(SongwriterState state, SongDecision decision)
    {
        var updated = state with
        {
            Lyrics = decision.Lyrics,
            Revision = state.Revision + 1,
            ProofreaderAccepted = null,
        };
        return updated;
    }

    public static SongwriterState Lint(SongwriterState state) =>
        state with
        {
            LintFeedback =
                state.Lyrics?.Contains('\n') == true
                    ? null
                    : "Lyrics must contain more than one line.",
        };

    public static SongwriterState ApplyProofread(
        SongwriterState state,
        ProofreaderDecision decision
    ) =>
        state with
        {
            ProofreaderFeedback = decision.Feedback,
            ProofreaderAccepted = decision.Accepted,
        };
}

public sealed class SongDecisionValidator : AbstractValidator<SongDecision>
{
    public SongDecisionValidator()
    {
        RuleFor(decision => decision.Lyrics).NotEmpty();
    }
}

public sealed class ProofreaderDecisionValidator : AbstractValidator<ProofreaderDecision>
{
    public ProofreaderDecisionValidator()
    {
        RuleFor(decision => decision.Feedback).NotEmpty();
    }
}
