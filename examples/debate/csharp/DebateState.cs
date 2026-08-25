namespace Examples.Debate;

public sealed record DebateState(
    string Question,
    IReadOnlyList<DebateArgument> Arguments,
    int Round,
    DebateVerdict? Verdict,
    bool? CritiqueAccepted = null
)
{
    public DebateState RecordProposal(ProposalDecision decision) =>
        this with
        {
            Arguments = [.. Arguments, new DebateArgument("proposer", decision.Text)],
            Round = Round + 1,
            CritiqueAccepted = null,
        };

    public DebateState RecordCritique(CritiqueDecision decision) =>
        this with
        {
            Arguments = [.. Arguments, new DebateArgument("critic", decision.Critique)],
            CritiqueAccepted = decision.Accepted,
        };

    public DebateState RecordVerdict(SubmitVerdict verdict) =>
        this with
        {
            Verdict = new DebateVerdict(verdict.Verdict, verdict.Reason),
        };
}

public sealed record DebateArgument(string Speaker, string Text);

public sealed record DebateVerdict(string Value, string Reason);

public sealed record ProposalDecision(string Text);

public sealed record CritiqueDecision(bool Accepted, string Critique);
