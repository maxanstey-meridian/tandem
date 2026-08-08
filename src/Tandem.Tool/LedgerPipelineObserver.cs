using Tandem.Ledger;

namespace Tandem.Tool;

internal sealed class LedgerPipelineObserver(RunLedger ledger) : IPipelineObserver
{
    private static readonly LedgerStream<RuntimeJournalRecord> _journal = new(
        "runtime.journal",
        "tandem.runtime-journal"
    );
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _nextEntry;

    public ValueTask RecordRunStartedAsync(CancellationToken cancellationToken) =>
        AppendAsync(new RuntimeJournalRecord(RuntimeJournalKind.RunStarted, ""), cancellationToken);

    public ValueTask RecordRunCompletedAsync(string result, CancellationToken cancellationToken) =>
        AppendAsync(
            new RuntimeJournalRecord(RuntimeJournalKind.RunCompleted, "", Result: result),
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
                OutcomeKind: value.Outcome.Kind
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
                value.RequestType
            ),
            PipelineInteractionAnsweredObservation value => new RuntimeJournalRecord(
                RuntimeJournalKind.InteractionAnswered,
                value.StepId,
                value.RequestId,
                value.ResponseType
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
                OutcomeKind: value.OutcomeKind
            ),
            PipelineCapabilityAccepted value => new RuntimeJournalRecord(
                RuntimeJournalKind.CapabilityAccepted,
                value.StepId,
                value.InvocationId,
                value.CapabilityName,
                OutcomeKind: value.CapabilityId
            ),
            _ => null,
        };
        return record is null ? ValueTask.CompletedTask : AppendAsync(record, cancellationToken);
    }

    private async ValueTask AppendAsync(
        RuntimeJournalRecord record,
        CancellationToken cancellationToken
    )
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var entry = ++_nextEntry;
            await ledger.AppendAsync(_journal, $"runtime--{entry:D12}", record, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

internal sealed class CompositePipelineObserver(params IPipelineObserver[] observers)
    : IPipelineObserver
{
    public async ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        foreach (var observer in observers)
        {
            await observer.ObserveAsync(observation, cancellationToken);
        }
    }
}
