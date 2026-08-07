using FluentAssertions;

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
                    PlannerDecisionValue.Proceed,
                    "Proceed with a hidden condition.",
                    ["Do another thing."],
                    ["README.md"]
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
        var validator = new ReviewDecisionValidator(["outcome"]);
        var delivered = new ReviewOutcomeAssessment("outcome", true, ["src/service.ts"]);

        validator
            .Validate(
                new ReviewDecision(
                    ReviewDecisionValue.NeedsHuman,
                    "Need input.",
                    [delivered],
                    [],
                    null
                )
            )
            .IsValid.Should()
            .BeFalse();
        validator
            .Validate(
                new ReviewDecision(
                    ReviewDecisionValue.RequestChanges,
                    "Changes required.",
                    [delivered],
                    [new ReviewFinding(ReviewFindingSeverity.High, "", "service.ts")]
                )
            )
            .IsValid.Should()
            .BeFalse();
    }

    [Fact]
    public void ReviewValidator_RejectsCheeseAndIncompleteOutcomeCoverage()
    {
        var validator = new ReviewDecisionValidator(["first", "second"]);

        var result = validator.Validate(
            new ReviewDecision(
                ReviewDecisionValue.Accept,
                "todo",
                [new ReviewOutcomeAssessment("first", true, ["src/first.ts"])],
                []
            )
        );

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == "Summary");
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("second"));
    }

    [Fact]
    public void ReviewValidator_RequiresActionableReasonForRequestChanges()
    {
        var validator = new ReviewDecisionValidator(["outcome"]);

        validator
            .Validate(
                new ReviewDecision(
                    ReviewDecisionValue.RequestChanges,
                    "The candidate requires another pass.",
                    [new ReviewOutcomeAssessment("outcome", true, ["src/service.ts"])],
                    []
                )
            )
            .IsValid.Should()
            .BeFalse();
    }
}
