namespace Tandem.Sample.Debate;

public sealed record DebateState(
    string Question,
    string WorkspacePath,
    IReadOnlyList<DebateArgument> Arguments,
    int Round,
    DebateVerdict? Verdict
);

public sealed record DebateArgument(string Speaker, string Text);

public sealed record DebateVerdict(string Value, string Reason);

public sealed record ProposalDecision(string Text);

public sealed record CritiqueDecision(bool Accepted, string Critique);
