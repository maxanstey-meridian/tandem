using FluentValidation;
using Tandem.Advanced;

namespace Tandem.Sample.Debate;

public static class DebatePolicies
{
    public static AgentConversationDecision DiscardJudgeAfterVerdict(
        AgentMessageContext<DebateState> _,
        AgentMessageOutcome __
    ) => new(AgentConversationRetention.Discard);

    public static DebateState ApplyProposal(DebateState state, ProposalDecision decision) =>
        state with
        {
            Arguments = [.. state.Arguments, new DebateArgument("proposer", decision.Text)],
            Round = state.Round + 1,
            CritiqueAccepted = null,
        };

    public static DebateState ApplyCritique(DebateState state, CritiqueDecision decision) =>
        state with
        {
            Arguments = [.. state.Arguments, new DebateArgument("critic", decision.Critique)],
            CritiqueAccepted = decision.Accepted,
        };

    public static DebateState ApplyVerdict(DebateState state, SubmitVerdict verdict) =>
        state with
        {
            Verdict = new DebateVerdict(verdict.Verdict, verdict.Reason),
        };
}

public sealed class ProposalDecisionValidator : AbstractValidator<ProposalDecision>
{
    public ProposalDecisionValidator()
    {
        RuleFor(decision => decision.Text).NotEmpty();
    }
}

public sealed class CritiqueDecisionValidator : AbstractValidator<CritiqueDecision>
{
    public CritiqueDecisionValidator()
    {
        RuleFor(decision => decision.Critique).NotEmpty();
    }
}
