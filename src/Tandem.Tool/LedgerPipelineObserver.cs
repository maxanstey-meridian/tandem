using Tandem.Ledger;

namespace Tandem.Tool;

internal sealed class LedgerPipelineObserver(RunLedger ledger) : IPipelinePersistenceObserver
{
    internal static readonly LedgerStream<RuntimeJournalRecord> Journal = new(
        "runtime.journal",
        "tandem.runtime-journal"
    );
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _nextEntry;

    public ValueTask RecordRunStartedAsync(CancellationToken cancellationToken) =>
        AppendAsync(
            new RuntimeJournalRecord(RuntimeJournalKind.RunStarted, ""),
            entryId: null,
            cancellationToken
        );

    public ValueTask RecordRunCompletedAsync(string result, CancellationToken cancellationToken) =>
        AppendAsync(
            new RuntimeJournalRecord(RuntimeJournalKind.RunCompleted, "", Result: result),
            entryId: null,
            cancellationToken
        );

    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        var record = observation switch
        {
            PipelineStepStarted value => new RuntimeJournalRecord(
                RuntimeJournalKind.StepStarted,
                value.StepId
            ),
            PipelineStepCompleted value => new RuntimeJournalRecord(
                RuntimeJournalKind.StepCompleted,
                value.StepId,
                Result: value.Outcome.Summary,
                OutcomeKind: value.Outcome.Kind,
                ValueType: value.AcceptedValue?.ValueType,
                Payload: value.AcceptedValue?.Payload
            ),
            PipelineStepFaulted value => new RuntimeJournalRecord(
                RuntimeJournalKind.StepFaulted,
                value.StepId,
                Result: value.Error
            ),
            PipelineStepCancelled value => new RuntimeJournalRecord(
                RuntimeJournalKind.StepCancelled,
                value.StepId
            ),
            PipelineInteractionRequestedObservation value => new RuntimeJournalRecord(
                RuntimeJournalKind.InteractionRequested,
                value.StepId,
                value.RequestId,
                value.RequestType,
                ValueType: value.RequestType,
                Payload: value.Payload
            ),
            PipelineInteractionAnsweredObservation value => new RuntimeJournalRecord(
                RuntimeJournalKind.InteractionAnswered,
                value.StepId,
                value.RequestId,
                value.ResponseType,
                ValueType: value.ResponseType,
                Payload: value.Payload
            ),
            PipelineCommandOutput value => new RuntimeJournalRecord(
                RuntimeJournalKind.CommandCompleted,
                value.StepId,
                Name: value.Command,
                Result: value.ExitCode.ToString()
            ),
            PipelineAgentUsage value => new RuntimeJournalRecord(
                RuntimeJournalKind.UsageRecorded,
                value.StepId,
                InputTokens: value.InputTokens,
                OutputTokens: value.OutputTokens,
                CurrentContextTokens: value.CurrentContextTokens
            ),
            PipelineActionAttempted value => new RuntimeJournalRecord(
                RuntimeJournalKind.ActionAttempted,
                value.StepId,
                value.InvocationId,
                value.ActionName,
                value.Effect
            ),
            PipelineActionCompleted value => new RuntimeJournalRecord(
                RuntimeJournalKind.ActionCompleted,
                value.StepId,
                value.InvocationId,
                value.ActionName,
                value.Effect,
                value.Result
            ),
            PipelineStructuredOutputAccepted value => new RuntimeJournalRecord(
                RuntimeJournalKind.StructuredOutputAccepted,
                value.StepId,
                value.AcceptedOutputId,
                OutcomeKind: value.OutcomeKind,
                ValueType: value.OutputType,
                Payload: value.Payload
            ),
            PipelineCapabilityAccepted value => new RuntimeJournalRecord(
                RuntimeJournalKind.CapabilityAccepted,
                value.StepId,
                value.AcceptedCallId,
                value.CapabilityName,
                OutcomeKind: value.CapabilityId,
                ValueType: value.RequestType,
                Payload: value.Payload
            ),
            _ => null,
        };
        return record is null
            ? ValueTask.CompletedTask
            : AppendAsync(record, EntryId(observation), cancellationToken);
    }

    private static string? EntryId(PipelineObservation observation) =>
        observation switch
        {
            PipelineStructuredOutputAccepted value => $"accepted-output--{value.AcceptedOutputId}",
            PipelineCapabilityAccepted value => $"accepted-capability--{value.AcceptedCallId}",
            PipelineInteractionRequestedObservation value =>
                $"interaction-request--{value.RequestId}",
            PipelineInteractionAnsweredObservation value =>
                $"interaction-response--{value.RequestId}",
            _ => null,
        };

    private async ValueTask AppendAsync(
        RuntimeJournalRecord record,
        string? entryId,
        CancellationToken cancellationToken
    )
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var entry = ++_nextEntry;
            await ledger.AppendAsync(
                Journal,
                entryId ?? $"runtime--{entry:D12}",
                record,
                cancellationToken
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

internal sealed class CompositePipelineObserver(
    IPipelinePersistenceObserver persistenceObserver,
    params IPipelineObserver[] observers
) : IPipelinePersistenceObserver
{
    public async ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        await persistenceObserver.ObserveAsync(observation, cancellationToken);
        foreach (var observer in observers)
        {
            await observer.ObserveAsync(observation, cancellationToken);
        }
    }
}
