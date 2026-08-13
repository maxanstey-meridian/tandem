using System.Text.Json;

namespace Tandem.Ledger;

public enum RuntimeJournalKind
{
    RunStarted,
    RunCompleted,
    StepStarted,
    StepCompleted,
    StepFaulted,
    StepCancelled,
    InteractionRequested,
    InteractionAnswered,
    CommandCompleted,
    UsageRecorded,
    ActionAttempted,
    ActionCompleted,
    StructuredOutputAccepted,
    CapabilityAccepted,
}

public sealed record RuntimeJournalRecord(
    RuntimeJournalKind Kind,
    string StepId,
    string? Identity = null,
    string? Name = null,
    string? Effect = null,
    string? Result = null,
    string? OutcomeKind = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? CurrentContextTokens = null,
    string? ValueType = null,
    JsonElement? Payload = null,
    int? ContextWindowTokens = null
);
