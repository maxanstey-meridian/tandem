using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Sample.Support;

public static class SupportPolicies
{
    public const string CategorizedOutcome = "support.categorized";
    public const string ResolutionProposedOutcome = "support.resolution.proposed";

    public static AgentSessionDecision StartClassificationFresh(SupportState _) =>
        new(AgentSessionAction.Reset, "Classify each ticket from a fresh session.");

    public static AgentSessionDecision StartResolutionFresh(SupportState _) =>
        new(AgentSessionAction.Reset, "Resolve each classified ticket from a fresh session.");

    public static CustomerQuestion BuildCustomerQuestion(SupportState state) =>
        new(
            state.Ticket,
            state.ProposedResolution
                ?? throw new InvalidOperationException("A proposed resolution is required.")
        );

    public static SupportState ApplyCustomerReply(SupportState state, CustomerReply reply) =>
        state with
        {
            CustomerReply = reply.Text,
            FinalDisposition = reply.Resolved ? "closed" : "escalated",
        };

    public static StructuredOutputResult<SupportState> ParseClassification(
        string text,
        SupportState state
    ) =>
        Parse(
            text,
            root =>
            {
                var category = RequiredString(root, "category");
                return new StructuredOutcome<SupportState>(
                    CategorizedOutcome,
                    $"Classified as {category}.",
                    root,
                    state with
                    {
                        Category = category,
                    }
                );
            }
        );

    public static StructuredOutputResult<SupportState> ParseResolution(
        string text,
        SupportState state
    ) =>
        Parse(
            text,
            root =>
            {
                var proposal = RequiredString(root, "proposedResolution");
                return new StructuredOutcome<SupportState>(
                    ResolutionProposedOutcome,
                    "Proposed a customer-facing resolution.",
                    root,
                    state with
                    {
                        ProposedResolution = proposal,
                    }
                );
            }
        );

    private static StructuredOutputResult<SupportState> Parse(
        string text,
        Func<JsonElement, StructuredOutcome<SupportState>> map
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

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName).GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{propertyName} must not be blank.")
            : value;
    }
}
