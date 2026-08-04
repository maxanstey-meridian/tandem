namespace Tandem.Domain;

public sealed record Outcome(string Id, string Description);

public sealed record Packet(
    string Title,
    string Repository,
    string Base,
    IReadOnlyList<Outcome> Outcomes,
    IReadOnlyList<string> Verification,
    IReadOnlyList<string> Constraints,
    string ImplementationContext
);
