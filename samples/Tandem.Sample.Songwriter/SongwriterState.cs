namespace Tandem.Sample.Songwriter;

public sealed record SongwriterState(
    string Brief,
    string? Lyrics = null,
    string? LintFeedback = null,
    string? ProofreaderFeedback = null,
    int Revision = 0,
    bool? ProofreaderAccepted = null
)
{
    public SongwriterState RecordSong(SongDecision decision) =>
        this with
        {
            Lyrics = decision.Lyrics,
            Revision = Revision + 1,
            ProofreaderAccepted = null,
        };

    public SongwriterState RecordProofread(ProofreaderDecision decision) =>
        this with
        {
            ProofreaderFeedback = decision.Feedback,
            ProofreaderAccepted = decision.Accepted,
        };
}

public sealed record SongDecision(
    [property: System.ComponentModel.Description("The complete song lyrics.")] string Lyrics
);

public sealed record LintDecision(bool Passed, string Feedback);

public sealed record ProofreaderDecision(bool Accepted, string Feedback);
