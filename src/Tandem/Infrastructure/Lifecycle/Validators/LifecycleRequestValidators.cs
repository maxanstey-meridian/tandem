using FluentValidation;
using Tandem.Domain;

namespace Tandem.Infrastructure.Lifecycle.Validators;

public sealed class AskPlannerRequestValidator : AbstractValidator<AskPlannerRequest>
{
    public AskPlannerRequestValidator()
    {
        RuleFor(request => request.Question).NotEmpty();
        RuleFor(request => request.ProposedApproach).NotEmpty();
        RuleFor(request => request.Evidence).NotEmpty();
        RuleForEach(request => request.Evidence).NotEmpty();
    }
}

public sealed class SubmitReportRequestValidator : AbstractValidator<SubmitReportRequest>
{
    public SubmitReportRequestValidator()
    {
        RuleFor(request => request.Summary).NotEmpty();
        RuleFor(request => request.Outcomes).NotEmpty();
        RuleForEach(request => request.Outcomes).NotEmpty();
        RuleFor(request => request.Evidence).NotEmpty();
        RuleForEach(request => request.Evidence).NotEmpty();
    }
}

public sealed class WriteCheckpointRequestValidator : AbstractValidator<WriteCheckpointRequest>
{
    public WriteCheckpointRequestValidator()
    {
        RuleFor(request => request.Summary).NotEmpty();
        RuleFor(request => request.Completed).NotNull();
        RuleForEach(request => request.Completed).NotEmpty();
        RuleFor(request => request.Next).NotNull();
        RuleForEach(request => request.Next).NotEmpty();
    }
}
