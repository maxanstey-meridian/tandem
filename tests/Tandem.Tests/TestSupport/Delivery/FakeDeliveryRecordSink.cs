namespace Tandem.Tests;

internal sealed class FakeDeliveryRecordSink : IDeliveryRecordSink
{
    public DeliveryLedgerContext Context { get; set; } = new(null, null, null, [], [], [], []);
    public PublicationCandidateDocument? PublicationCandidate { get; set; }
    public bool FailPublicationResults { get; set; }
    public List<PublicationResultRecord> PublicationResults { get; } = [];
    public bool FailCapabilities { get; set; }
    public bool FailHumanAnswers { get; set; }
    public bool FailVerificationResults { get; set; }
    public List<(string AcceptedCallId, string CapabilityName)> CapabilityAttempts { get; } = [];
    public List<(string RequestId, HumanAnswer Answer)> HumanAnswerAttempts { get; } = [];
    public List<(
        string AcceptedResultId,
        VerificationResult Result
    )> VerificationAttempts { get; } = [];
    public List<(
        string AcceptedCallId,
        ProgressCheckpointRecord Checkpoint
    )> CheckpointAttempts { get; } = [];

    public ValueTask<DeliveryLedgerContext> ReadContextAsync(
        DeliveryLedgerRole role,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(Context);

    public ValueTask AcceptCapabilityAsync<TRequest>(
        string acceptedCallId,
        string capabilityName,
        TRequest request,
        CancellationToken cancellationToken
    )
        where TRequest : class
    {
        CapabilityAttempts.Add((acceptedCallId, capabilityName));
        return FailCapabilities
            ? ValueTask.FromException(new IOException("Capability persistence failed."))
            : ValueTask.CompletedTask;
    }

    public ValueTask AcceptPlannerDecisionAsync(
        string acceptedOutputId,
        PlannerDecision decision,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask AcceptReviewDecisionAsync(
        string acceptedOutputId,
        ReviewDecision decision,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask AcceptCheckpointAsync(
        string acceptedCallId,
        ProgressCheckpointRecord checkpoint,
        CancellationToken cancellationToken
    )
    {
        CheckpointAttempts.Add((acceptedCallId, checkpoint));
        return ValueTask.CompletedTask;
    }

    public ValueTask AcceptReportAsync(
        string acceptedCallId,
        SubmitReportRequest report,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask AcceptPublicationCandidateAsync(
        string acceptedCandidateId,
        PublicationCandidateDocument candidate,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask<PublicationCandidateDocument?> ReadPublicationCandidateAsync(
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(PublicationCandidate);

    public ValueTask AcceptPublicationResultAsync(
        PublicationResultRecord result,
        CancellationToken cancellationToken
    )
    {
        if (FailPublicationResults)
        {
            return ValueTask.FromException(new IOException("Publication persistence failed."));
        }
        PublicationResults.Add(result);
        return ValueTask.CompletedTask;
    }

    public ValueTask AcceptTerminalOutcomeAsync(
        string terminalOutcomeId,
        TerminalOutcomeRecord outcome,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask AcceptHumanAnswerAsync(
        string requestId,
        string interactionId,
        HumanQuestion question,
        HumanAnswer answer,
        CancellationToken cancellationToken
    )
    {
        HumanAnswerAttempts.Add((requestId, answer));
        return FailHumanAnswers
            ? ValueTask.FromException(new IOException("Human answer persistence failed."))
            : ValueTask.CompletedTask;
    }

    public ValueTask AcceptVerificationResultAsync(
        string acceptedResultId,
        VerificationResult result,
        CancellationToken cancellationToken
    )
    {
        VerificationAttempts.Add((acceptedResultId, result));
        return FailVerificationResults
            ? ValueTask.FromException(new IOException("Verification persistence failed."))
            : ValueTask.CompletedTask;
    }
}
