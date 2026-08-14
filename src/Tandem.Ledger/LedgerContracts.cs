namespace Tandem.Ledger;

public sealed record LedgerStream<TEntry>(string Name, string Contract, int Version = 1)
{
    internal string ValidatedName => LedgerName.Validate(Name, nameof(Name));
    internal string ValidatedContract => LedgerName.Validate(Contract, nameof(Contract));
    internal int ValidatedVersion => LedgerName.ValidateVersion(Version);
}

public sealed record LedgerDocument<TDocument>(string Name, string Contract, int Version = 1)
{
    internal string ValidatedName => LedgerName.Validate(Name, nameof(Name));
    internal string ValidatedContract => LedgerName.Validate(Contract, nameof(Contract));
    internal int ValidatedVersion => LedgerName.ValidateVersion(Version);
}

public sealed record SqliteLedgerOptions(
    TimeSpan BusyTimeout,
    int LockRetryAttempts,
    TimeSpan LockRetryDelay
)
{
    public static SqliteLedgerOptions Default { get; } =
        new(TimeSpan.FromSeconds(5), 2, TimeSpan.FromMilliseconds(50));
}

public sealed record AcceptedLedgerEntry<TEntry>(
    long Sequence,
    string EntryId,
    TEntry Value,
    DateTimeOffset RecordedAt
);

public sealed record LedgerDocumentValue<TDocument>(
    long Version,
    TDocument Value,
    DateTimeOffset UpdatedAt
);

public enum LedgerRunStatus
{
    Running,
    Ready,
    Failed,
    Faulted,
    Interrupted,
    Cancelled,
}

public sealed record LedgerRun(
    Guid RunId,
    string Composition,
    LedgerRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? EndedAt
);

public sealed record AcceptedPipelineValue<TValue>(
    long Sequence,
    string StepId,
    string ValueType,
    TValue Value,
    DateTimeOffset RecordedAt
);

public sealed class LedgerConflictException(string message) : InvalidOperationException(message);

public sealed class LedgerValueTypeMismatchException(string message)
    : InvalidOperationException(message);

public sealed class LedgerDataException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

internal static class LedgerName
{
    public static string Validate(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Ledger names cannot be blank.", parameterName)
            : value;

    public static int ValidateVersion(int value) =>
        value < 1
            ? throw new ArgumentOutOfRangeException(nameof(value), "Contract versions start at 1.")
            : value;
}
