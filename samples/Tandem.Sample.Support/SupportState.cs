namespace Tandem.Sample.Support;

public sealed record SupportState(
    string Ticket,
    string CustomerId,
    string? Category = null,
    string? AccountContext = null,
    string? ProposedResolution = null,
    string? CustomerReply = null,
    string? FinalDisposition = null
);

public sealed record ClassificationDecision(string Category);

public sealed record ResolutionDecision(string ProposedResolution);

public sealed record CustomerQuestion(string Ticket, string ProposedResolution);

public sealed record CustomerReply(string Text, bool Resolved);

public interface IAccountLookup
{
    public ValueTask<string> LoadAsync(SupportState state, CancellationToken cancellationToken);
}

public static class SupportIds
{
    public const string CustomerReply = "CustomerReply";
}
