using FluentValidation;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public sealed class PlannerDecisionValidator : AbstractValidator<PlannerDecision>
{
    private static readonly HashSet<string> _placeholders = new(
        ["todo", "lgtm", "done", "looks good", "n/a"],
        StringComparer.OrdinalIgnoreCase
    );

    public PlannerDecisionValidator()
    {
        RuleFor(decision => decision.Rationale).Must(BeMeaningful);
        RuleFor(decision => decision.EvidenceUsed).NotEmpty();
        RuleForEach(decision => decision.EvidenceUsed).Must(BeMeaningful);
        RuleFor(decision => decision.Constraints)
            .Empty()
            .When(decision => decision.Decision == PlannerDecisionValue.Proceed);
        RuleFor(decision => decision.Constraints)
            .NotEmpty()
            .When(decision => decision.Decision == PlannerDecisionValue.ProceedWithConstraints);
        RuleForEach(decision => decision.Constraints).Must(BeMeaningful);
        RuleFor(decision => decision.HumanQuestion)
            .NotEmpty()
            .Must(question => !string.Equals(question, "N/A", StringComparison.OrdinalIgnoreCase))
            .When(decision => decision.Decision == PlannerDecisionValue.NeedsHuman);
        RuleFor(decision => decision.HumanQuestion)
            .Null()
            .When(decision => decision.Decision != PlannerDecisionValue.NeedsHuman);
    }

    internal static bool BeMeaningful(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !_placeholders.Contains(value.Trim());
}

public sealed class ReviewDecisionValidator : AbstractValidator<ReviewDecision>
{
    public ReviewDecisionValidator(IEnumerable<string>? expectedOutcomeIds = null)
    {
        var expected = expectedOutcomeIds?.ToHashSet(StringComparer.Ordinal) ?? [];

        RuleFor(decision => decision.Summary).Must(PlannerDecisionValidator.BeMeaningful);
        RuleFor(decision => decision.Outcomes).NotNull();
        RuleForEach(decision => decision.Outcomes)
            .SetValidator(new ReviewOutcomeAssessmentValidator());
        RuleFor(decision => decision.Findings).NotNull();
        RuleForEach(decision => decision.Findings).SetValidator(new ReviewFindingValidator());
        RuleFor(decision => decision)
            .Custom((decision, context) => ValidateOutcomeCoverage(decision, expected, context));
        RuleFor(decision => decision.Findings)
            .NotEmpty()
            .When(decision =>
                decision.Decision == ReviewDecisionValue.RequestChanges
                && decision.Outcomes?.All(outcome => outcome.Delivered) == true
            );
        RuleFor(decision => decision.HumanQuestion)
            .NotEmpty()
            .Must(question => !string.Equals(question, "N/A", StringComparison.OrdinalIgnoreCase))
            .When(decision => decision.Decision == ReviewDecisionValue.NeedsHuman);
        RuleFor(decision => decision.HumanQuestion)
            .Null()
            .When(decision => decision.Decision != ReviewDecisionValue.NeedsHuman);
    }

    private static void ValidateOutcomeCoverage(
        ReviewDecision decision,
        IReadOnlySet<string> expected,
        ValidationContext<ReviewDecision> context
    )
    {
        if (expected.Count == 0)
        {
            return;
        }

        var outcomes = decision.Outcomes ?? [];
        var groups = outcomes.GroupBy(outcome => outcome.OutcomeId, StringComparer.Ordinal);
        foreach (var group in groups.Where(group => group.Count() > 1))
        {
            context.AddFailure("outcomes", $"Outcome '{group.Key}' must be assessed exactly once.");
        }
        foreach (var unknown in outcomes.Where(outcome => !expected.Contains(outcome.OutcomeId)))
        {
            context.AddFailure("outcomes", $"Unknown outcome '{unknown.OutcomeId}'.");
        }
        foreach (var missing in expected.Except(outcomes.Select(outcome => outcome.OutcomeId)))
        {
            context.AddFailure("outcomes", $"Missing assessment for outcome '{missing}'.");
        }
        if (
            decision.Decision == ReviewDecisionValue.Accept
            && outcomes.Any(outcome => !outcome.Delivered)
        )
        {
            context.AddFailure("outcomes", "Accept requires every packet outcome to be delivered.");
        }
    }
}

public sealed class ReviewOutcomeAssessmentValidator : AbstractValidator<ReviewOutcomeAssessment>
{
    public ReviewOutcomeAssessmentValidator()
    {
        RuleFor(outcome => outcome.OutcomeId).Must(PlannerDecisionValidator.BeMeaningful);
        RuleFor(outcome => outcome.Evidence).NotEmpty();
        RuleForEach(outcome => outcome.Evidence).Must(PlannerDecisionValidator.BeMeaningful);
    }
}

public sealed class ReviewFindingValidator : AbstractValidator<ReviewFinding>
{
    public ReviewFindingValidator()
    {
        RuleFor(finding => finding.Severity).IsInEnum();
        RuleFor(finding => finding.Description).Must(PlannerDecisionValidator.BeMeaningful);
        RuleFor(finding => finding.Evidence).Must(PlannerDecisionValidator.BeMeaningful);
    }
}
