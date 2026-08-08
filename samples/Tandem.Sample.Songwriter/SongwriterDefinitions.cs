using Microsoft.Extensions.AI;

namespace Tandem.Sample.Songwriter;

public sealed record SongwriterClients(IChatClient Songwriter, IChatClient Proofreader);

public static class SongwriterDefinitions
{
    public static SongwriterSteps Create(AgentFactory agents, SongwriterClients clients) =>
        new(
            Create(
                "songwriter",
                "Write or revise lyrics from the brief and current feedback.",
                clients.Songwriter,
                new SongDecisionValidator(),
                SongwriterPolicies.ApplySong,
                agents
            ),
            new LintStage(),
            Create(
                "proofreader",
                "Proofread lyrics and either accept them or request changes.",
                clients.Proofreader,
                new ProofreaderDecisionValidator(),
                SongwriterPolicies.ApplyProofread,
                agents
            ),
            PipelineNodes.Complete<SongwriterState>("complete"),
            PipelineNodes.Failed<SongwriterState>("songwriter-failed")
        );

    private static AgentDefinition<SongwriterState> Create<TOutput>(
        string id,
        string instructions,
        IChatClient client,
        FluentValidation.IValidator<TOutput> validator,
        Func<SongwriterState, TOutput, SongwriterState> apply,
        AgentFactory agents
    ) =>
        agents
            .Create<SongwriterState>(id, instructions, client)
            .WithMessage(state =>
                $"Brief: {state.Brief}\nLyrics: {state.Lyrics}\n"
                + $"Lint: {state.LintFeedback}\nProofreader: {state.ProofreaderFeedback}"
            )
            .WithOutput(validator, apply)
            .Build();
}
