using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Sample.Debate;

public static class DebatePolicies
{
    public static AgentSessionDecision RetainRevisionContext(PipelineMessage<DebateState> _) =>
        new(AgentSessionAction.Continue, "Retain critic context across revision rounds.");

    public static AgentSessionDecision StartJudgeFresh(PipelineMessage<DebateState> _) =>
        new(AgentSessionAction.Reset, "Judge each accepted argument from a fresh session.");

    public static AgentTeardownDecision ReleaseJudgeAfterVerdict(
        PipelineMessage<DebateState> _,
        BlockOutcome __
    ) => new(true, true, "Release judge bookkeeping after an accepted verdict.");

    public static StructuredOutputResult<DebateState> ParseProposal(
        string text,
        PipelineMessage<DebateState> pipeline
    ) =>
        Parse(
            text,
            root =>
            {
                var proposal = root.GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(proposal))
                {
                    throw new InvalidOperationException("Proposal text must not be blank.");
                }
                var state = pipeline.State with
                {
                    Arguments =
                    [
                        .. pipeline.State.Arguments,
                        new DebateArgument("proposer", proposal),
                    ],
                    Round = pipeline.State.Round + 1,
                };
                return new StructuredOutcome<DebateState>("debate.proposed", proposal, root, state);
            }
        );

    public static StructuredOutputResult<DebateState> ParseCritique(
        string text,
        PipelineMessage<DebateState> pipeline
    ) =>
        Parse(
            text,
            root =>
            {
                var accepted = root.GetProperty("accepted").GetBoolean();
                var critique = root.GetProperty("critique").GetString();
                if (string.IsNullOrWhiteSpace(critique))
                {
                    throw new InvalidOperationException("Critique must not be blank.");
                }
                var state = pipeline.State with
                {
                    Arguments =
                    [
                        .. pipeline.State.Arguments,
                        new DebateArgument("critic", critique),
                    ],
                };
                return new StructuredOutcome<DebateState>(
                    accepted ? "debate.critique.accepted" : "debate.revision.requested",
                    critique,
                    root,
                    state
                );
            }
        );

    public static DebateState ApplyVerdict(DebateState state, string kind, JsonElement payload) =>
        kind == SubmitVerdictAction.OutcomeKind
            ? state with
            {
                Verdict = new DebateVerdict(
                    payload.GetProperty("verdict").GetString()!,
                    payload.GetProperty("reason").GetString()!
                ),
            }
            : state;

    private static StructuredOutputResult<DebateState> Parse(
        string text,
        Func<JsonElement, StructuredOutcome<DebateState>> map
    )
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(text);
            return new StructuredOutputResult<DebateState>(map(root), [], text, root);
        }
        catch (Exception exception)
            when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new StructuredOutputResult<DebateState>(
                null,
                [new StructuredOutputProblem("$", exception.Message)],
                text
            );
        }
    }
}
