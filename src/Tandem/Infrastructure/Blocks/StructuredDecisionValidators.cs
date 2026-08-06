using FluentValidation;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class PlannerDecisionValidator : AbstractValidator<PlannerDecision>
{
    public PlannerDecisionValidator()
    {
        RuleFor(decision => decision.Rationale).NotEmpty();
        RuleFor(decision => decision.EvidenceUsed).NotEmpty();
        RuleForEach(decision => decision.EvidenceUsed).NotEmpty();
        RuleFor(decision => decision.Constraints)
            .NotEmpty()
            .When(decision => decision.Decision == PlannerDecisionValue.ProceedWithConstraints);
        RuleForEach(decision => decision.Constraints).NotEmpty();
        RuleFor(decision => decision.HumanQuestion)
            .NotEmpty()
            .Must(question => !string.Equals(question, "N/A", StringComparison.OrdinalIgnoreCase))
            .When(decision => decision.Decision == PlannerDecisionValue.NeedsHuman);
        RuleFor(decision => decision.HumanQuestion)
            .Null()
            .When(decision => decision.Decision != PlannerDecisionValue.NeedsHuman);
    }
}

public sealed class ReviewDecisionValidator : AbstractValidator<ReviewDecision>
{
    public ReviewDecisionValidator()
    {
        RuleFor(decision => decision.Summary).NotEmpty();
        RuleFor(decision => decision.Findings).NotNull();
        RuleForEach(decision => decision.Findings).SetValidator(new ReviewFindingValidator());
        RuleFor(decision => decision.HumanQuestion)
            .NotEmpty()
            .Must(question => !string.Equals(question, "N/A", StringComparison.OrdinalIgnoreCase))
            .When(decision => decision.Decision == ReviewDecisionValue.NeedsHuman);
        RuleFor(decision => decision.HumanQuestion)
            .Null()
            .When(decision => decision.Decision != ReviewDecisionValue.NeedsHuman);
    }
}

public sealed class ReviewFindingValidator : AbstractValidator<ReviewFinding>
{
    public ReviewFindingValidator()
    {
        RuleFor(finding => finding.Severity).IsInEnum();
        RuleFor(finding => finding.Description).NotEmpty();
        RuleFor(finding => finding.Evidence).NotEmpty();
    }
}
