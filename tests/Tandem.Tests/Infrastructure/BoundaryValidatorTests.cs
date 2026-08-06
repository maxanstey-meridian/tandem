using FluentAssertions;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;
using Tandem.Infrastructure.Lifecycle.Validators;

namespace Tandem.Tests.Infrastructure;

public sealed class BoundaryValidatorTests
{
    [Fact]
    public void LifecycleValidators_RejectWhitespaceCollectionMembers()
    {
        new AskPlannerRequestValidator()
            .Validate(new AskPlannerRequest("Question", "Approach", [" "]))
            .IsValid.Should()
            .BeFalse();
        new SubmitReportRequestValidator()
            .Validate(new SubmitReportRequest("Summary", ["Outcome"], [""]))
            .IsValid.Should()
            .BeFalse();
        new WriteCheckpointRequestValidator()
            .Validate(new WriteCheckpointRequest("Summary", ["Done"], [" "]))
            .IsValid.Should()
            .BeFalse();
    }

    [Fact]
    public void PlannerValidator_EnforcesDecisionSpecificFields()
    {
        var validator = new PlannerDecisionValidator();

        validator
            .Validate(
                new PlannerDecision(
                    PlannerDecisionValue.Proceed,
                    "Proceed.",
                    [],
                    ["README.md"],
                    "N/A"
                )
            )
            .IsValid.Should()
            .BeFalse();
        validator
            .Validate(
                new PlannerDecision(
                    PlannerDecisionValue.NeedsHuman,
                    "A decision is required.",
                    [],
                    ["README.md"]
                )
            )
            .IsValid.Should()
            .BeFalse();
        validator
            .Validate(
                new PlannerDecision(
                    PlannerDecisionValue.ProceedWithConstraints,
                    "Proceed carefully.",
                    [],
                    ["README.md"]
                )
            )
            .IsValid.Should()
            .BeFalse();
    }

    [Fact]
    public void ReviewValidator_EnforcesHumanQuestionAndFindingContent()
    {
        var validator = new ReviewDecisionValidator();

        validator
            .Validate(new ReviewDecision(ReviewDecisionValue.NeedsHuman, "Need input.", [], null))
            .IsValid.Should()
            .BeFalse();
        validator
            .Validate(
                new ReviewDecision(
                    ReviewDecisionValue.RequestChanges,
                    "Changes required.",
                    [new ReviewFinding(ReviewFindingSeverity.High, "", "service.ts")]
                )
            )
            .IsValid.Should()
            .BeFalse();
    }
}
