namespace Tandem;

public static class StandardOutcomeKinds
{
    public const string Success = "tandem.success";
    public const string Failed = "tandem.failed";
}

public abstract record Outcome<TState>
{
    private Outcome() { }

    public sealed record Success(TState State) : Outcome<TState>;

    public sealed record Failed(TState State, FailureEvidence Failure) : Outcome<TState>;
}

public sealed record FailureEvidence(string Code, string Summary, string? Detail = null);
