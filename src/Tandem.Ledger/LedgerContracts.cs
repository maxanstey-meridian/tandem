namespace Tandem.Ledger;

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

public sealed record LedgerJournalEntry(
    long Sequence,
    RuntimeJournalRecord Record,
    DateTimeOffset RecordedAt
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
