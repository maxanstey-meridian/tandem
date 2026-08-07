using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Sample.Debate;

public sealed class DebateStepsFactory(AgentRuntime agentRuntime, DebateOptions options)
{
    public DebateSteps Create(PipelineBuildContext context) =>
        new(
            new OpenDebateStage(),
            new ProposerAgent(
                CreateStructured(
                    ProposerAgent.StepId,
                    options.ProposerClient,
                    DebatePolicies.ParseProposal,
                    chat =>
                        chat.ResponseFormat = ChatResponseFormat.ForJsonSchema<ProposalDecision>(),
                    context
                )
            ),
            new CriticAgent(
                CreateStructured(
                    CriticAgent.StepId,
                    options.CriticClient,
                    DebatePolicies.ParseCritique,
                    chat =>
                        chat.ResponseFormat = ChatResponseFormat.ForJsonSchema<CritiqueDecision>(),
                    context
                )
            ),
            new JudgeAgent(
                agentRuntime
                    .Create<DebateState>(
                        JudgeAgent.StepId,
                        JudgeAgent.StepId,
                        "Judge the accepted argument and submit a verdict.",
                        options.JudgeClient
                    )
                    .WithMessage(state => $"Judge: {state.Question}")
                    .WithLifecycleActions(
                        DebateRegistration.LifecycleIdentity,
                        [SubmitVerdictAction.ToolName],
                        DebatePolicies.ApplyVerdict
                    )
                    .WithSessionPolicy(DebatePolicies.StartJudgeFresh)
                    .WithTeardownPolicy(DebatePolicies.ReleaseJudgeAfterVerdict)
                    .Build(context)
            ),
            new CompleteDebateStage(),
            PipelineNodes.Failed<DebateState>("debate-failed")
        );

    private AgentOperation<DebateState> CreateStructured(
        string id,
        IChatClient client,
        StructuredOutputParser<DebateState> parser,
        Action<ChatOptions> configureChatOptions,
        PipelineBuildContext context
    ) =>
        agentRuntime
            .Create<DebateState>(
                id,
                id,
                $"Act as the debate {id} and return structured JSON.",
                client
            )
            .WithMessage(state => $"Question: {state.Question}; round: {state.Round}")
            .WithStructuredOutput(parser, configureChatOptions)
            .WithSessionPolicy(DebatePolicies.RetainRevisionContext)
            .Build(context);
}
