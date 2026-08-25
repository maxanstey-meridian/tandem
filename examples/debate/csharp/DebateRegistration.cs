using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem;

namespace Examples.Debate;

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
            new SubmitVerdictCapability(),
            (state, request) => state.RecordVerdict(request)
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

public sealed class SubmitVerdictCapability : IAgentCapabilityDefinition<DebateState, SubmitVerdict>
{
    public string ToolName => "submit_verdict";
    public string Instructions => "Submit the final debate verdict and end the judge turn.";
    public IValidator<SubmitVerdict> Validator { get; } = new SubmitVerdictValidator();

    public string Summarize(SubmitVerdict request) => $"Verdict submitted: {request.Verdict}";
}
