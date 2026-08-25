using FluentValidation;
using Tandem;

namespace Examples.CodeWriter;

public sealed class SubmitImplementationValidator : AbstractValidator<SubmitImplementation>
{
    public SubmitImplementationValidator()
    {
        RuleFor(submission => submission.Implementation).NotEmpty();
        RuleFor(submission => submission.Rationale).NotEmpty();
    }
}

public sealed class SubmitImplementationCapability
    : IAgentCapabilityDefinition<CodeWriterState, SubmitImplementation>
{
    public string ToolName => "submit_implementation";

    public string Instructions =>
        "Submit the complete JavaScript implementation and its rationale.";

    public IValidator<SubmitImplementation> Validator { get; } =
        new SubmitImplementationValidator();

    public string Summarize(SubmitImplementation request) =>
        $"Implementation:\n{request.Implementation}\n\nRationale:\n{request.Rationale}";
}

public sealed class ReviewDecisionValidator : AbstractValidator<ReviewDecision>
{
    public ReviewDecisionValidator()
    {
        RuleFor(review => review.Decision).IsInEnum();
        RuleFor(review => review.Summary).NotEmpty();
        RuleForEach(review => review.Findings).NotEmpty();
        RuleFor(review => review.Findings)
            .NotEmpty()
            .When(review => review.Decision == ReviewDisposition.RequestChanges)
            .WithMessage("RequestChanges requires at least one finding.");
    }
}

public sealed class ReviewDecisionOutput : IAgentOutputDefinition<CodeWriterState, ReviewDecision>
{
    public string Instructions =>
        "Return Accept or RequestChanges with a concise summary and concrete findings.";

    public IValidator<ReviewDecision> Validator { get; } = new ReviewDecisionValidator();
}
