using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Tandem.Sample.Debate;

public sealed record DebateOptions(
    IChatClient ProposerClient,
    IChatClient CriticClient,
    IChatClient JudgeClient
);

public static class DebateRegistration
{
    public static IServiceCollection AddDebate(
        this IServiceCollection services,
        DebateOptions options
    )
    {
        var verdict = AgentCapabilities.Create<DebateState, SubmitVerdict>(
            "submit_verdict",
            "Submit the final debate verdict and end the judge turn.",
            new SubmitVerdictValidator(),
            request => $"Verdict submitted: {request.Verdict}",
            DebatePolicies.ApplyVerdict
        );
        services.AddSingleton(verdict);
        services.AddSingleton(options);
        services.AddSingleton(sp =>
            DebateDefinitions.Create(sp.GetRequiredService<DebateOptions>(), verdict)
        );
        services.AddSingleton<DebateComposition>();
        return services;
    }
}

public sealed record SubmitVerdict(string Verdict, string Reason);

public sealed class SubmitVerdictValidator : AbstractValidator<SubmitVerdict>
{
    public SubmitVerdictValidator()
    {
        RuleFor(request => request.Verdict).NotEmpty();
        RuleFor(request => request.Reason).NotEmpty();
    }
}
