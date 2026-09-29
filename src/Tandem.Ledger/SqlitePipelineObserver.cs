using System.Text.Json;

namespace Tandem.Ledger;

public sealed class SqlitePipelineObserver : IPipelinePersistenceObserver
{
    private readonly SqliteLedgerStore _store;
    private readonly Guid _runId;

    internal SqlitePipelineObserver(SqliteLedgerStore store, Guid runId)
    {
        _store = store;
        _runId = runId;
    }

    public ValueTask RecordRunStartedAsync(CancellationToken cancellationToken = default) =>
        AppendAsync(new RuntimeJournalRecord(RuntimeJournalKind.RunStarted, ""), cancellationToken);

    public ValueTask RecordRunCompletedAsync(
        string result,
        CancellationToken cancellationToken = default
    ) =>
        AppendAsync(
            new RuntimeJournalRecord(RuntimeJournalKind.RunCompleted, "", Result: result),
            cancellationToken
        );

    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        if (observation.RunId != _runId)
        {
            throw new LedgerConflictException(
                $"Observer for run '{_runId:N}' cannot persist observation for run '{observation.RunId:N}'."
            );
        }
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
                ValueType: value.Payload is null ? null : value.RequestType,
                Payload: value.Payload
            ),
            PipelineInteractionAnsweredObservation value => new RuntimeJournalRecord(
                RuntimeJournalKind.InteractionAnswered,
                value.StepId,
                value.RequestId,
                value.ResponseType,
                ValueType: value.Payload is null ? null : value.ResponseType,
                Payload: value.Payload
            ),
            PipelineCommandOutput value => new RuntimeJournalRecord(
                RuntimeJournalKind.CommandCompleted,
                value.StepId,
                Name: value.Command,
                Result: value.ExitCode.ToString(),
                ValueType: typeof(string).FullName,
                Payload: JsonSerializer.SerializeToElement(value.Output)
            ),
            PipelineAgentUsage value => new RuntimeJournalRecord(
                RuntimeJournalKind.UsageRecorded,
                value.StepId,
                InputTokens: value.InputTokens,
                OutputTokens: value.OutputTokens,
                CurrentContextTokens: value.CurrentContextTokens,
                ContextWindowTokens: value.ContextWindowTokens > 0
                    ? value.ContextWindowTokens
                    : null
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
                value.Result,
                Payload: value.Process is null
                    ? null
                    : JsonSerializer.SerializeToElement(value.Process)
            ),
            PipelineStructuredOutputRejected value => new RuntimeJournalRecord(
                RuntimeJournalKind.StructuredOutputRejected,
                value.StepId,
                Identity: value.Attempt.ToString(),
                ValueType: typeof(StructuredOutputRejectionEvidence).FullName,
                Payload: JsonSerializer.SerializeToElement(
                    new StructuredOutputRejectionEvidence(
                        value.Attempt,
                        value.Problems,
                        value.RawResponse
                    )
                )
            ),
            PipelineStructuredOutputAccepted value => new RuntimeJournalRecord(
                RuntimeJournalKind.StructuredOutputAccepted,
                value.StepId,
                value.AcceptedOutputId,
                OutcomeKind: value.OutcomeKind,
                ValueType: value.Payload is null ? null : value.OutputType,
                Payload: value.Payload
            ),
            PipelineCapabilityAccepted value => new RuntimeJournalRecord(
                RuntimeJournalKind.CapabilityAccepted,
                value.StepId,
                value.AcceptedCallId,
                value.CapabilityName,
                OutcomeKind: value.CapabilityId,
                ValueType: value.Payload is null ? null : value.RequestType,
                Payload: value.Payload
            ),
            _ => null,
        };
        return record is null
            ? ValueTask.CompletedTask
            : AppendAsync(record with { VisitId = observation.VisitId }, cancellationToken);
    }

    private async ValueTask AppendAsync(
        RuntimeJournalRecord record,
        CancellationToken cancellationToken
    ) => await _store.AppendAsync(_runId, record, cancellationToken);
}
