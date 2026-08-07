using FluentValidation;
using Tandem.Domain;

namespace Tandem.Sample.Support;

public static class SupportPolicies
{
    public static AgentSessionDecision StartClassificationFresh(SupportState _) =>
        new(AgentSessionAction.Reset, "Classify each ticket from a fresh session.");

    public static AgentSessionDecision StartResolutionFresh(SupportState _) =>
        new(AgentSessionAction.Reset, "Resolve each classified ticket from a fresh session.");

    public static CustomerQuestion BuildCustomerQuestion(SupportState state) =>
        new(
            state.Ticket,
            state.ProposedResolution
                ?? throw new InvalidOperationException("A proposed resolution is required.")
        );

    public static SupportState ApplyCustomerReply(SupportState state, CustomerReply reply) =>
        state with
        {
            CustomerReply = reply.Text,
            FinalDisposition = reply.Resolved ? "closed" : "escalated",
        };

    public static SupportState ApplyClassification(
        SupportState state,
        ClassificationDecision decision
    ) => state with { Category = decision.Category };

    public static SupportState ApplyResolution(SupportState state, ResolutionDecision decision) =>
        state with
        {
            ProposedResolution = decision.ProposedResolution,
        };
}

public sealed class ClassificationDecisionValidator : AbstractValidator<ClassificationDecision>
{
    public ClassificationDecisionValidator()
    {
        RuleFor(decision => decision.Category).NotEmpty();
    }
}

public sealed class ResolutionDecisionValidator : AbstractValidator<ResolutionDecision>
{
    public ResolutionDecisionValidator()
    {
        RuleFor(decision => decision.ProposedResolution).NotEmpty();
    }
}
