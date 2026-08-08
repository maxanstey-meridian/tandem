using Microsoft.Extensions.AI;

namespace Tandem.Sample.Songwriter;

public sealed record SongwriterClients(IChatClient Songwriter, IChatClient Proofreader);

public static class SongwriterDefinitions
{
    public static SongwriterParticipants Create(SongwriterClients clients) =>
        new(
            Create(
                "songwriter",
                "Write or revise lyrics from the brief and current feedback.",
                clients.Songwriter,
                new SongDecisionOutput(),
                (state, decision) => state.RecordSong(decision)
            ),
            new LintStage(),
            Create(
                "proofreader",
                "Proofread lyrics and either accept them or request changes.",
                clients.Proofreader,
                new ProofreaderDecisionOutput(),
                (state, decision) => state.RecordProofread(decision)
            ),
            PipelineNodes.Complete<SongwriterState>("complete"),
            PipelineNodes.Failed<SongwriterState>("songwriter-failed")
        );

    private static AgentDefinition<SongwriterState> Create<TOutput>(
        string id,
        string instructions,
        IChatClient client,
        IAgentOutputDefinition<SongwriterState, TOutput> output,
        Func<SongwriterState, TOutput, SongwriterState> apply
    ) =>
        Agent
            .Create<SongwriterState>(id, instructions, client)
            .WithMessage(state =>
                $"Brief: {state.Brief}\nLyrics: {state.Lyrics}\n"
                + $"Lint: {state.LintFeedback}\nProofreader: {state.ProofreaderFeedback}"
            )
            .WithOutput(output, apply)
            .Build();
}
