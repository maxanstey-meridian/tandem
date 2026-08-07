using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;
using Tandem.Infrastructure.Lifecycle;

namespace Tandem.Sample.Debate;

public sealed record DebateOptions(
    string TandemHome,
    string ExecutablePath,
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
        services.AddSingleton(new ProposerAgent(CreateProposer(options)));
        services.AddSingleton(new CriticAgent(CreateCritic(options)));
        services.AddSingleton(new JudgeAgent(CreateJudge(options)));
        services.AddSingleton<DebateSteps>();
        services.AddSingleton<DebateComposition>();
        return services;
    }

    private static AgentBlock<DebateState> CreateProposer(DebateOptions options) =>
        CreateStructured("proposer", options.ProposerClient, DebatePolicies.ParseProposal, options);

    private static AgentBlock<DebateState> CreateCritic(DebateOptions options) =>
        CreateStructured("critic", options.CriticClient, DebatePolicies.ParseCritique, options);

    private static AgentBlock<DebateState> CreateStructured(
        string id,
        IChatClient client,
        StructuredOutputParser<DebateState> parser,
        DebateOptions options
    ) =>
        new(
            new AgentBlockConfig<DebateState>(
                id,
                id,
                $"Act as the debate {id} and return structured JSON.",
                [],
                pipeline => $"Question: {pipeline.State.Question}; round: {pipeline.State.Round}",
                state => state.WorkspacePath,
                _ => false,
                StructuredOutput: parser,
                SessionPolicy: DebatePolicies.RetainRevisionContext
            ),
            client,
            options.TandemHome,
            options.ExecutablePath,
            configureChatOptions: chat =>
                chat.ResponseFormat =
                    id == "proposer"
                        ? ChatResponseFormat.ForJsonSchema<ProposalDecision>()
                        : ChatResponseFormat.ForJsonSchema<CritiqueDecision>()
        );

    private static AgentBlock<DebateState> CreateJudge(DebateOptions options) =>
        new(
            new AgentBlockConfig<DebateState>(
                "judge",
                "judge",
                "Judge the accepted argument and submit a verdict.",
                [SubmitVerdictAction.ToolName],
                pipeline => $"Judge: {pipeline.State.Question}",
                state => state.WorkspacePath,
                _ => false,
                ReceiptTransition: DebatePolicies.ApplyVerdict,
                LifecycleActionSetIdentity: LifecycleIdentity,
                SessionPolicy: DebatePolicies.StartJudgeFresh,
                TeardownPolicy: DebatePolicies.ReleaseJudgeAfterVerdict
            ),
            options.JudgeClient,
            options.TandemHome,
            options.ExecutablePath
        );
}
