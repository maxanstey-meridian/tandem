using Tandem.Delivery;
using Tandem.Ledger;

namespace Tandem.Tool;

internal sealed class DeliveryLedger(RunLedger ledger) : IDeliveryRecordSink
{
    private const int RecentRecordLimit = 5;
    private static readonly LedgerStream<PlannerDecisionRecord> _plannerDecisions = new(
        "delivery.planner-decisions",
        "delivery.planner-decision"
    );
    private static readonly LedgerStream<ReviewDecisionRecord> _reviewDecisions = new(
        "delivery.review-decisions",
        "delivery.review-decision"
    );
    private static readonly LedgerStream<HumanAnswerRecord> _humanAnswers = new(
        "delivery.human-answers",
        "delivery.human-answer"
    );
    private static readonly LedgerStream<VerificationResultRecord> _verificationResults = new(
        "delivery.verification-results",
        "delivery.verification-result"
    );
    private static readonly LedgerStream<ProgressCheckpointRecord> _checkpoints = new(
        "delivery.progress-checkpoints",
        "delivery.progress-checkpoint"
    );
    private static readonly LedgerStream<TerminalOutcomeRecord> _terminalOutcomes = new(
        "delivery.terminal-outcomes",
        "delivery.terminal-outcome"
    );
    private static readonly LedgerStream<PublicationResultRecord> _publicationResults = new(
        "delivery.publication-results",
        "delivery.publication-result"
    );
    private static readonly LedgerDocument<OutcomeProgressDocument> _outcomes = new(
        "delivery.outcomes",
        "delivery.outcome-progress"
    );
    private static readonly LedgerDocument<AcceptedImplementationReportDocument> _report = new(
        "delivery.implementation-report",
        "delivery.implementation-report"
    );
    private static readonly LedgerDocument<PublicationCandidateDocument> _publicationCandidate =
        new("delivery.publication-candidate", "delivery.publication-candidate");

    public async ValueTask<DeliveryLedgerContext> ReadContextAsync(
        DeliveryLedgerRole role,
        CancellationToken cancellationToken
    )
    {
        var outcomes = (await ledger.ReadDocumentAsync(_outcomes, cancellationToken))?.Value;
        var report =
            role == DeliveryLedgerRole.Reviewer
                ? (await ledger.ReadDocumentAsync(_report, cancellationToken))?.Value
                : null;
        var checkpoints =
            role == DeliveryLedgerRole.Executor
                ? await ledger.ReadRecentAsync(_checkpoints, 1, cancellationToken)
                : [];
        var plannerDecisions = await ledger.ReadRecentAsync(
            _plannerDecisions,
            RecentRecordLimit,
            cancellationToken
        );
        var reviews =
            role == DeliveryLedgerRole.Reviewer
                ? await ledger.ReadRecentAsync(
                    _reviewDecisions,
                    RecentRecordLimit,
                    cancellationToken
                )
                : [];
        var verification = role is DeliveryLedgerRole.Executor or DeliveryLedgerRole.Reviewer
            ? await ledger.ReadRecentAsync(
                _verificationResults,
                RecentRecordLimit,
                cancellationToken
            )
            : [];
        var humanAnswers =
            role == DeliveryLedgerRole.Reviewer
                ? await ledger.ReadRecentAsync(_humanAnswers, RecentRecordLimit, cancellationToken)
                : [];
        return new DeliveryLedgerContext(
            outcomes,
            report,
            checkpoints.LastOrDefault()?.Value,
            plannerDecisions.Select(entry => entry.Value).ToArray(),
            reviews.Select(entry => entry.Value).ToArray(),
            verification.Select(entry => entry.Value).ToArray(),
            humanAnswers.Select(entry => entry.Value).ToArray()
        );
    }

    public async ValueTask InitializeAsync(Packet packet, CancellationToken cancellationToken)
    {
        await WriteAcceptedDocumentAsync(
            _outcomes,
            new OutcomeProgressDocument(
                "packet",
                packet
                    .Outcomes.Select(outcome => new OutcomeProgress(
                        outcome.Id,
                        outcome.Description,
                        Delivered: false,
                        Evidence: []
                    ))
                    .ToArray()
            ),
            "packet",
            document => document.AcceptedDecisionId,
            cancellationToken
        );
    }

    public async ValueTask AcceptCapabilityAsync<TRequest>(
        string acceptedCallId,
        string capabilityName,
        TRequest request,
        CancellationToken cancellationToken
    )
        where TRequest : class =>
        await ledger.AppendAsync(
            new LedgerStream<CapabilityAcceptedRecord<TRequest>>(
                $"delivery.capabilities.{capabilityName}",
                $"delivery.capability.{capabilityName}"
            ),
            acceptedCallId,
            new CapabilityAcceptedRecord<TRequest>(capabilityName, request),
            cancellationToken
        );

    public async ValueTask AcceptPlannerDecisionAsync(
        string acceptedOutputId,
        PlannerDecision decision,
        CancellationToken cancellationToken
    ) =>
        await ledger.AppendAsync(
            _plannerDecisions,
            acceptedOutputId,
            new PlannerDecisionRecord(decision),
            cancellationToken
        );

    public async ValueTask AcceptReviewDecisionAsync(
        string acceptedOutputId,
        ReviewDecision decision,
        CancellationToken cancellationToken
    )
    {
        await ledger.AppendAsync(
            _reviewDecisions,
            acceptedOutputId,
            new ReviewDecisionRecord(decision),
            cancellationToken
        );
        var current = await ledger.ReadDocumentAsync(_outcomes, cancellationToken);
        if (current is null)
        {
            throw new InvalidOperationException("Delivery outcomes were not initialized.");
        }
        var descriptions = current.Value.Outcomes.ToDictionary(
            outcome => outcome.Id,
            outcome => outcome.Description,
            StringComparer.Ordinal
        );
        await WriteAcceptedDocumentAsync(
            _outcomes,
            new OutcomeProgressDocument(
                acceptedOutputId,
                decision
                    .Outcomes.Select(outcome => new OutcomeProgress(
                        outcome.OutcomeId,
                        descriptions[outcome.OutcomeId],
                        outcome.Delivered,
                        outcome.Evidence
                    ))
                    .ToArray()
            ),
            acceptedOutputId,
            document => document.AcceptedDecisionId,
            cancellationToken
        );
    }

    public async ValueTask AcceptCheckpointAsync(
        string acceptedCallId,
        ProgressCheckpointRecord checkpoint,
        CancellationToken cancellationToken
    ) => await ledger.AppendAsync(_checkpoints, acceptedCallId, checkpoint, cancellationToken);

    public async ValueTask AcceptReportAsync(
        string acceptedCallId,
        SubmitReportRequest report,
        CancellationToken cancellationToken
    ) =>
        await WriteAcceptedDocumentAsync(
            _report,
            new AcceptedImplementationReportDocument(acceptedCallId, report),
            acceptedCallId,
            document => document.AcceptedCallId,
            cancellationToken
        );

    public async ValueTask AcceptPublicationCandidateAsync(
        string acceptedCandidateId,
        PublicationCandidateDocument candidate,
        CancellationToken cancellationToken
    ) =>
        await WriteAcceptedDocumentAsync(
            _publicationCandidate,
            candidate,
            acceptedCandidateId,
            document => document.AcceptedCandidateId,
            cancellationToken
        );

    public async ValueTask<PublicationCandidateDocument?> ReadPublicationCandidateAsync(
        CancellationToken cancellationToken
    ) => (await ledger.ReadDocumentAsync(_publicationCandidate, cancellationToken))?.Value;

    public async ValueTask AcceptPublicationResultAsync(
        PublicationResultRecord result,
        CancellationToken cancellationToken
    ) =>
        await ledger.AppendAsync(
            _publicationResults,
            $"publication--{result.Branch}--{result.CandidateSha}",
            result,
            cancellationToken
        );

    public async ValueTask AcceptTerminalOutcomeAsync(
        string terminalOutcomeId,
        TerminalOutcomeRecord outcome,
        CancellationToken cancellationToken
    ) => await ledger.AppendAsync(_terminalOutcomes, terminalOutcomeId, outcome, cancellationToken);

    public async ValueTask AcceptHumanAnswerAsync(
        string requestId,
        string interactionId,
        HumanQuestion question,
        HumanAnswer answer,
        CancellationToken cancellationToken
    ) =>
        await ledger.AppendAsync(
            _humanAnswers,
            $"interaction--{requestId}",
            new HumanAnswerRecord(requestId, interactionId, question, answer),
            cancellationToken
        );

    public async ValueTask AcceptVerificationResultAsync(
        string acceptedResultId,
        VerificationResult result,
        CancellationToken cancellationToken
    ) =>
        await ledger.AppendAsync(
            _verificationResults,
            acceptedResultId,
            new VerificationResultRecord(result),
            cancellationToken
        );

    private async ValueTask WriteAcceptedDocumentAsync<TDocument>(
        LedgerDocument<TDocument> document,
        TDocument value,
        string acceptedId,
        Func<TDocument, string> identity,
        CancellationToken cancellationToken
    )
    {
        var current = await ledger.ReadDocumentAsync(document, cancellationToken);
        var expectedVersion =
            current is null ? 0
            : string.Equals(identity(current.Value), acceptedId, StringComparison.Ordinal)
                ? current.Version - 1
            : current.Version;
        await ledger.WriteDocumentAsync(document, value, expectedVersion, cancellationToken);
    }
}
