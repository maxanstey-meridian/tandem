using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Sample.Songwriter;

public sealed record SongwriterClients(IChatClient Songwriter, IChatClient Proofreader);

public sealed class SongwriterStepsFactory(AgentRuntime agents, SongwriterClients clients)
{
    public SongwriterSteps Create(PipelineBuildContext context) =>
        new(
            new SongwriterAgent(
                Create(
                    SongwriterAgent.StepId,
                    "Write or revise lyrics from the brief and current feedback.",
                    clients.Songwriter,
                    SongwriterPolicies.ParseSong,
                    chat => chat.ResponseFormat = ChatResponseFormat.ForJsonSchema<SongDecision>(),
                    context
                )
            ),
            new LintStage(),
            new ProofreaderAgent(
                Create(
                    ProofreaderAgent.StepId,
                    "Proofread lyrics and either accept them or request changes.",
                    clients.Proofreader,
                    SongwriterPolicies.ParseProofread,
                    chat =>
                        chat.ResponseFormat =
                            ChatResponseFormat.ForJsonSchema<ProofreaderDecision>(),
                    context
                )
            ),
            new CompleteSongStage(),
            PipelineNodes.Failed<SongwriterState>("songwriter-failed")
        );

    private AgentOperation<SongwriterState> Create(
        string id,
        string instructions,
        IChatClient client,
        StructuredOutputParser<SongwriterState> parser,
        Action<ChatOptions> configureChatOptions,
        PipelineBuildContext context
    ) =>
        agents
            .Create<SongwriterState>(id, id, instructions, client)
            .WithMessage(state =>
                $"Brief: {state.Brief}\nLyrics: {state.Lyrics}\n"
                + $"Lint: {state.LintFeedback}\nProofreader: {state.ProofreaderFeedback}"
            )
            .WithStructuredOutput(parser, configureChatOptions)
            .WithSessionPolicy(SongwriterPolicies.StartFresh)
            .Build(context);
}
