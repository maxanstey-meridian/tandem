using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Actions;
using Tandem.Domain;

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
        services.AddSingleton<OpenDebateStage>();
        services.AddSingleton<CompleteDebateStage>();
        services.AddSingleton(sp => new ProposerAgent(
            CreateProposer(sp.GetRequiredService<AgentRuntime>(), options)
        ));
        services.AddSingleton(sp => new CriticAgent(
            CreateCritic(sp.GetRequiredService<AgentRuntime>(), options)
        ));
        services.AddSingleton(sp => new JudgeAgent(
            CreateJudge(sp.GetRequiredService<AgentRuntime>(), options)
        ));
        services.AddSingleton<DebateSteps>();
        services.AddSingleton<DebateComposition>();
        return services;
    }

    private static AgentOperation<DebateState> CreateProposer(
        AgentRuntime agentRuntime,
        DebateOptions options
    ) =>
        CreateStructured(
            agentRuntime,
            ProposerAgent.StepId,
            options.ProposerClient,
            DebatePolicies.ParseProposal,
            chat => chat.ResponseFormat = ChatResponseFormat.ForJsonSchema<ProposalDecision>()
        );

    private static AgentOperation<DebateState> CreateCritic(
        AgentRuntime agentRuntime,
        DebateOptions options
    ) =>
        CreateStructured(
            agentRuntime,
            CriticAgent.StepId,
            options.CriticClient,
            DebatePolicies.ParseCritique,
            chat => chat.ResponseFormat = ChatResponseFormat.ForJsonSchema<CritiqueDecision>()
        );

    private static AgentOperation<DebateState> CreateStructured(
        AgentRuntime agentRuntime,
        string id,
        IChatClient client,
        StructuredOutputParser<DebateState> parser,
        Action<ChatOptions> configureChatOptions
    ) =>
        agentRuntime
            .Create<DebateState>(
                id,
                id,
                $"Act as the debate {id} and return structured JSON.",
                client
            )
            .WithMessage(pipeline =>
                $"Question: {pipeline.State.Question}; round: {pipeline.State.Round}"
            )
            .WithStructuredOutput(parser, configureChatOptions)
            .WithSessionPolicy(DebatePolicies.RetainRevisionContext)
            .Build();

    private static AgentOperation<DebateState> CreateJudge(
        AgentRuntime agentRuntime,
        DebateOptions options
    ) =>
        agentRuntime
            .Create<DebateState>(
                JudgeAgent.StepId,
                JudgeAgent.StepId,
                "Judge the accepted argument and submit a verdict.",
                options.JudgeClient
            )
            .WithMessage(pipeline => $"Judge: {pipeline.State.Question}")
            .WithLifecycleActions(
                LifecycleIdentity,
                [SubmitVerdictAction.ToolName],
                DebatePolicies.ApplyVerdict
            )
            .WithSessionPolicy(DebatePolicies.StartJudgeFresh)
            .WithTeardownPolicy(DebatePolicies.ReleaseJudgeAfterVerdict)
            .Build();
}
