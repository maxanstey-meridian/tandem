namespace Tandem.Sample.Songwriter;

public sealed record SongwriterState(
    string Brief,
    string? Lyrics = null,
    string? LintFeedback = null,
    string? ProofreaderFeedback = null,
    int Revision = 0,
    bool? ProofreaderAccepted = null
);

public sealed record SongDecision(string Lyrics);

public sealed record LintDecision(bool Passed, string Feedback);

public sealed record ProofreaderDecision(bool Accepted, string Feedback);
