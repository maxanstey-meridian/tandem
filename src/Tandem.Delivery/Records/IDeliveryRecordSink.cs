namespace Tandem.Delivery;

public interface IDeliveryRecordSink
{
    public ValueTask<DeliveryLedgerContext> ReadContextAsync(
        DeliveryLedgerRole role,
        CancellationToken cancellationToken
    );

    public ValueTask AcceptCapabilityAsync<TRequest>(
        string acceptedCallId,
        string capabilityName,
        TRequest request,
        CancellationToken cancellationToken
    )
        where TRequest : class;

    public ValueTask AcceptPlannerDecisionAsync(
        string acceptedOutputId,
        PlannerDecision decision,
        CancellationToken cancellationToken
    );

    public ValueTask AcceptReviewDecisionAsync(
        string acceptedOutputId,
        ReviewDecision decision,
        CancellationToken cancellationToken
    );

    public ValueTask AcceptCheckpointAsync(
        string acceptedCallId,
        ProgressCheckpointRecord checkpoint,
        CancellationToken cancellationToken
    );

    public ValueTask AcceptReportAsync(
        string acceptedCallId,
        SubmitReportRequest report,
        CancellationToken cancellationToken
    );

    public ValueTask AcceptPublicationCandidateAsync(
        string acceptedCandidateId,
        PublicationCandidateDocument candidate,
        CancellationToken cancellationToken
    );

    public ValueTask<PublicationCandidateDocument?> ReadPublicationCandidateAsync(
        CancellationToken cancellationToken
    );

    public ValueTask AcceptPublicationResultAsync(
        PublicationResultRecord result,
        CancellationToken cancellationToken
    );

    public ValueTask AcceptTerminalOutcomeAsync(
        string terminalOutcomeId,
        TerminalOutcomeRecord outcome,
        CancellationToken cancellationToken
    );

    public ValueTask AcceptHumanAnswerAsync(
        string requestId,
        string interactionId,
        HumanQuestion question,
        HumanAnswer answer,
        CancellationToken cancellationToken
    );

    public ValueTask AcceptVerificationResultAsync(
        string acceptedResultId,
        VerificationResult result,
        CancellationToken cancellationToken
    );
}
