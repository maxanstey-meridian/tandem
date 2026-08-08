using FluentValidation;

namespace Tandem.Sample.Support;

public static class SupportPolicies { }

public sealed class ClassificationDecisionValidator : AbstractValidator<ClassificationDecision>
{
    public ClassificationDecisionValidator()
    {
        RuleFor(decision => decision.Category).NotEmpty();
    }
}

public sealed class ClassificationDecisionOutput
    : IAgentOutputDefinition<SupportState, ClassificationDecision>
{
    public string Instructions => "Return the support-ticket classification.";
    public IValidator<ClassificationDecision> Validator { get; } =
        new ClassificationDecisionValidator();
}

public sealed class ResolutionDecisionValidator : AbstractValidator<ResolutionDecision>
{
    public ResolutionDecisionValidator()
    {
        RuleFor(decision => decision.ProposedResolution).NotEmpty();
    }
}

public sealed class ResolutionDecisionOutput
    : IAgentOutputDefinition<SupportState, ResolutionDecision>
{
    public string Instructions => "Return the proposed customer resolution.";
    public IValidator<ResolutionDecision> Validator { get; } = new ResolutionDecisionValidator();
}
