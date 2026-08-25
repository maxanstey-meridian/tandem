using Microsoft.Extensions.AI;
using Tandem;

namespace Examples.Songwriter;

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
            PipelineNodes.Complete(new SongwriterComplete()),
            PipelineNodes.Failed(new SongwriterFailed())
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

public sealed class SongwriterComplete : IPipelineCompletion<SongwriterState>
{
    public string Id => "complete";

    public string Summarize(SongwriterState state) =>
        $"Song accepted after {state.Revision} revision(s)";
}

public sealed class SongwriterFailed : IPipelineFailure<SongwriterState>
{
    public string Id => "songwriter-failed";

    public string Summarize(SongwriterState state) =>
        state.ProofreaderFeedback ?? "Songwriting failed";
}
