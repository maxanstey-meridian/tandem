using Microsoft.Extensions.AI;
using Tandem.Advanced;

namespace Tandem.Sample.Debate;

public static class DebateDefinitions
{
    public static DebateSteps Create(
        AgentFactory agentRuntime,
        DebateOptions options,
        AgentCapability<DebateState> verdict
    ) =>
        new(
            new OpenDebateStage(),
            CreateStructured(
                "proposer",
                options.ProposerClient,
                new ProposalDecisionValidator(),
                DebatePolicies.ApplyProposal,
                agentRuntime
            ),
            CreateStructured(
                "critic",
                options.CriticClient,
                new CritiqueDecisionValidator(),
                DebatePolicies.ApplyCritique,
                agentRuntime
            ),
            agentRuntime
                .Create<DebateState>(
                    "judge",
                    "Judge the accepted argument and submit a verdict.",
                    options.JudgeClient
                )
                .WithMessage(state => $"Judge: {state.Question}")
                .WithCapability(verdict)
                .WithConversationPolicy(DebatePolicies.DiscardJudgeAfterVerdict)
                .Build(),
            PipelineNodes.Complete<DebateState>("complete"),
            PipelineNodes.Failed<DebateState>("debate-failed")
        );

    private static AgentDefinition<DebateState> CreateStructured<TOutput>(
        string id,
        IChatClient client,
        FluentValidation.IValidator<TOutput> validator,
        Func<DebateState, TOutput, DebateState> apply,
        AgentFactory agentRuntime
    ) =>
        agentRuntime
            .Create<DebateState>(id, $"Act as the debate {id} and return structured JSON.", client)
            .WithMessage(state => $"Question: {state.Question}; round: {state.Round}")
            .WithOutput(validator, apply)
            .ContinueSession()
            .Build();
}
