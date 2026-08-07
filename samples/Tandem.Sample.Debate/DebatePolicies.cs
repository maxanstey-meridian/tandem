using FluentValidation;
using Tandem.Domain;

namespace Tandem.Sample.Debate;

public static class DebatePolicies
{
    public static AgentSessionDecision RetainRevisionContext(DebateState _) =>
        new(AgentSessionAction.Continue, "Retain critic context across revision rounds.");

    public static AgentSessionDecision StartJudgeFresh(DebateState _) =>
        new(AgentSessionAction.Reset, "Judge each accepted argument from a fresh session.");

    public static AgentConversationDecision DiscardJudgeAfterVerdict(
        PipelineMessage<DebateState> _,
        BlockOutcome __
    ) => new(AgentConversationRetention.Discard, "The verdict closes the judge conversation.");

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
