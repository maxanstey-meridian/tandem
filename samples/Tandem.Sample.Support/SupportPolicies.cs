using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Sample.Support;

public static class SupportPolicies
{
    public const string CategorizedOutcome = "support.categorized";
    public const string ResolutionProposedOutcome = "support.resolution.proposed";

    public static AgentSessionDecision StartClassificationFresh(PipelineMessage<SupportState> _) =>
        new(AgentSessionAction.Reset, "Classify each ticket from a fresh session.");

    public static AgentSessionDecision StartResolutionFresh(PipelineMessage<SupportState> _) =>
        new(AgentSessionAction.Reset, "Resolve each classified ticket from a fresh session.");

    public static CustomerQuestion BuildCustomerQuestion(PipelineMessage<SupportState> pipeline) =>
        new(
            pipeline.State.Ticket,
            pipeline.State.ProposedResolution
                ?? throw new InvalidOperationException("A proposed resolution is required.")
        );

    public static PipelineMessage<SupportState> ApplyCustomerReply(
        PipelineMessage<SupportState> pipeline,
        CustomerReply reply
    ) =>
        pipeline with
        {
            State = pipeline.State with
            {
                CustomerReply = reply.Text,
                FinalDisposition = reply.Resolved ? "closed" : "escalated",
            },
            LatestOutcome = new BlockOutcome(
                reply.Resolved ? "support.customer.resolved" : "support.customer.blocked",
                SupportIds.ApplyReply,
                reply.Text,
                JsonSerializer.SerializeToElement(new { reply.Text, reply.Resolved })
            ),
        };

    public static StructuredOutputResult<SupportState> ParseClassification(
        string text,
        PipelineMessage<SupportState> pipeline
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
                    pipeline.State with
                    {
                        Category = category,
                    }
                );
            }
        );

    public static StructuredOutputResult<SupportState> ParseResolution(
        string text,
        PipelineMessage<SupportState> pipeline
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
                    pipeline.State with
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
