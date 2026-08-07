using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Sample.Songwriter;

public static class SongwriterPolicies
{
    public const string SongWrittenOutcome = "songwriter.written";
    public const string ProofAcceptedOutcome = "proofreader.accepted";
    public const string ChangesRequestedOutcome = "proofreader.changes-requested";

    public static StructuredOutputResult<SongwriterState> ParseSong(
        string text,
        SongwriterState state
    ) =>
        Parse(
            text,
            root =>
            {
                var lyrics = RequiredString(root, "lyrics");
                var updatedState = state with { Lyrics = lyrics, Revision = state.Revision + 1 };
                return new StructuredOutcome<SongwriterState>(
                    SongWrittenOutcome,
                    $"Wrote revision {updatedState.Revision}.",
                    root,
                    updatedState
                );
            }
        );

    public static SongwriterState Lint(SongwriterState state) =>
        state with
        {
            LintFeedback =
                state.Lyrics?.Contains('\n') == true
                    ? null
                    : "Lyrics must contain more than one line.",
        };

    public static StructuredOutputResult<SongwriterState> ParseProofread(
        string text,
        SongwriterState state
    ) =>
        Parse(
            text,
            root =>
            {
                var accepted = root.GetProperty("accepted").GetBoolean();
                var feedback = RequiredString(root, "feedback");
                return new StructuredOutcome<SongwriterState>(
                    accepted ? ProofAcceptedOutcome : ChangesRequestedOutcome,
                    feedback,
                    root,
                    state with
                    {
                        ProofreaderFeedback = feedback,
                    }
                );
            }
        );

    public static AgentSessionDecision StartFresh(SongwriterState _) =>
        new(AgentSessionAction.Reset, "Evaluate the latest durable song state afresh.");

    private static StructuredOutputResult<SongwriterState> Parse(
        string text,
        Func<JsonElement, StructuredOutcome<SongwriterState>> map
    )
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(text);
            return new(map(root), [], text, root);
        }
        catch (Exception exception)
            when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(null, [new("$", exception.Message)], text);
        }
    }

    private static string RequiredString(JsonElement root, string property)
    {
        var value = root.GetProperty(property).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{property} must not be blank.")
            : value;
    }
}
