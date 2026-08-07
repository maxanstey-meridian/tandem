using Tandem.Domain;

namespace Tandem.Sample.Support;

public static class SupportPrompts
{
    public const string Classifier =
        "Classify the customer support ticket into one concise category and return structured JSON.";

    public const string Resolver =
        "Propose a concise customer-facing resolution using the ticket and account context. Return structured JSON.";

    public static string ClassificationMessage(PipelineMessage<SupportState> pipeline) =>
        $"Ticket: {pipeline.State.Ticket}";

    public static string ResolutionMessage(PipelineMessage<SupportState> pipeline) =>
        $"Ticket: {pipeline.State.Ticket}\nCategory: {pipeline.State.Category}\nAccount: {pipeline.State.AccountContext}";
}
