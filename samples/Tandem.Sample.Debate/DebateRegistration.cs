using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Actions;

namespace Tandem.Sample.Debate;

public sealed record DebateOptions(
    IChatClient ProposerClient,
    IChatClient CriticClient,
    IChatClient JudgeClient
);

public static class DebateRegistration
{
    public const string LifecycleIdentity = "debate";

    public static IServiceCollection AddDebate(
        this IServiceCollection services,
        DebateOptions options
    )
    {
        services.AddSingleton(
            new LifecycleActionSetRegistration(
                LifecycleIdentity,
                actionServices => actionServices.AddMcpServer().WithTools<SubmitVerdictAction>()
            )
        );
        services.AddSingleton(options);
        services.AddSingleton<DebateStepsFactory>();
        services.AddSingleton<DebateComposition>();
        return services;
    }
}
