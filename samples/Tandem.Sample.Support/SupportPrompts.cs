namespace Tandem.Sample.Support;

public static class SupportPrompts
{
    public const string Classifier =
        "Classify the customer support ticket into one concise category and return structured JSON.";

    public const string Resolver =
        "Propose a concise customer-facing resolution using the ticket and account context. Return structured JSON.";

    public static string ClassificationMessage(SupportState state) => $"Ticket: {state.Ticket}";

    public static string ResolutionMessage(SupportState state) =>
        $"Ticket: {state.Ticket}\nCategory: {state.Category}\nAccount: {state.AccountContext}";
}
