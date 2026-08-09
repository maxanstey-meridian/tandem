using FluentValidation;
using Tandem.Advanced;

namespace Tandem.Sample.Debate;

public static class DebatePolicies
{
    public static AgentConversationDecision DiscardJudgeAfterVerdict(
        AgentMessageContext<DebateState> _,
        AgentMessageOutcome __
    ) => new(AgentConversationRetention.Discard);
}

public sealed class ProposalDecisionValidator : AbstractValidator<ProposalDecision>
{
    public ProposalDecisionValidator()
    {
        RuleFor(decision => decision.Text).NotEmpty();
    }
}

public sealed class ProposalDecisionOutput : IAgentOutputDefinition<DebateState, ProposalDecision>
{
    public string Instructions => "Return the proposed debate argument.";
    public IValidator<ProposalDecision> Validator { get; } = new ProposalDecisionValidator();
}

public sealed class CritiqueDecisionValidator : AbstractValidator<CritiqueDecision>
{
    public CritiqueDecisionValidator()
    {
        RuleFor(decision => decision.Critique).NotEmpty();
    }
}

public sealed class CritiqueDecisionOutput : IAgentOutputDefinition<DebateState, CritiqueDecision>
{
    public string Instructions => "Return the critique and whether the proposal is accepted.";
    public IValidator<CritiqueDecision> Validator { get; } = new CritiqueDecisionValidator();
}
