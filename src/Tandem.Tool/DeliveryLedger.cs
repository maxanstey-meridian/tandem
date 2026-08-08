using System.Text.Json;
using Tandem.Delivery;
using Tandem.Ledger;

namespace Tandem.Tool;

internal sealed class DeliveryLedger(RunLedger ledger) : IDeliveryRecordSink
{
    private const int RecentRecordLimit = 5;
    internal static readonly LedgerStream<VerificationResultRecord> VerificationResults = new(
        "delivery.verification-results",
        "delivery.verification-result"
    );
    private static readonly LedgerStream<ProgressCheckpointRecord> _checkpoints = new(
        "delivery.progress-checkpoints",
        "delivery.progress-checkpoint"
    );
    internal static readonly LedgerStream<PublicationResultRecord> PublicationResults = new(
        "delivery.publication-results",
        "delivery.publication-result"
    );
    private static readonly LedgerDocument<OutcomeProgressDocument> _outcomes = new(
        "delivery.outcomes",
        "delivery.outcome-progress"
    );
    internal static readonly LedgerDocument<PublicationCandidateDocument> PublicationCandidate =
        new("delivery.publication-candidate", "delivery.publication-candidate");

    public async ValueTask<DeliveryLedgerContext> ReadContextAsync(
        DeliveryLedgerRole role,
        CancellationToken cancellationToken
    )
    {
        var baseline = (await ledger.ReadDocumentAsync(_outcomes, cancellationToken))?.Value;
        var journal = (await ledger.ReadAsync(LedgerPipelineObserver.Journal, cancellationToken))
            .Select(entry => entry.Value)
            .ToArray();
        var plannerDecisions = journal
            .Where(record =>
                record.Kind == RuntimeJournalKind.StructuredOutputAccepted
                && record.StepId == DeliveryIds.Planner
                && record.Payload is not null
            )
            .Select(Deserialize<PlannerDecision>)
            .TakeLast(RecentRecordLimit)
            .ToArray();
        var reviews = journal
            .Where(record =>
                record.Kind == RuntimeJournalKind.StructuredOutputAccepted
                && record.StepId == DeliveryIds.Reviewer
                && record.Payload is not null
            )
            .Select(record => (Record: record, Decision: Deserialize<ReviewDecision>(record)))
            .TakeLast(RecentRecordLimit)
            .ToArray();
        var outcomes = ProjectOutcomes(baseline, reviews.LastOrDefault());
        var report =
            role == DeliveryLedgerRole.Reviewer
                ? journal.LastOrDefault(record =>
                    record.Kind == RuntimeJournalKind.CapabilityAccepted
                    && record.Name == "submit_report"
                    && record.Payload is not null
                )
                    is { } acceptedReport
                    ? Deserialize<SubmitReportRequest>(acceptedReport)
                    : null
                : null;
        var checkpoints =
            role == DeliveryLedgerRole.Executor
                ? await ledger.ReadRecentAsync(_checkpoints, 1, cancellationToken)
                : [];
        var verification = role is DeliveryLedgerRole.Executor or DeliveryLedgerRole.Reviewer
            ? await ledger.ReadRecentAsync(
                VerificationResults,
                RecentRecordLimit,
                cancellationToken
            )
            : [];
        var humanAnswers =
            role == DeliveryLedgerRole.Reviewer
                ? ProjectHumanAnswers(journal).TakeLast(RecentRecordLimit).ToArray()
                : [];
        return new DeliveryLedgerContext(
            outcomes,
            report,
            checkpoints.LastOrDefault()?.Value,
            plannerDecisions,
            role == DeliveryLedgerRole.Reviewer
                ? reviews.Select(entry => entry.Decision).ToArray()
                : [],
            verification.Select(entry => entry.Value.Result).ToArray(),
            humanAnswers
        );
    }

    private static OutcomeProgressDocument? ProjectOutcomes(
        OutcomeProgressDocument? baseline,
        (RuntimeJournalRecord? Record, ReviewDecision? Decision) latestReview
    )
    {
        if (baseline is null || latestReview.Record is null)
        {
            return baseline;
        }
        var assessments = latestReview.Decision!.Outcomes.ToDictionary(
            outcome => outcome.OutcomeId,
            StringComparer.Ordinal
        );
        return new OutcomeProgressDocument(
            latestReview.Record.Identity ?? baseline.AcceptedDecisionId,
            baseline
                .Outcomes.Select(outcome =>
                    assessments.TryGetValue(outcome.Id, out var assessment)
                        ? outcome with
                        {
                            Delivered = assessment.Delivered,
                            Evidence = assessment.Evidence,
                        }
                        : outcome
                )
                .ToArray()
        );
    }

    private static IEnumerable<HumanAnswerRecord> ProjectHumanAnswers(
        IReadOnlyList<RuntimeJournalRecord> journal
    )
    {
        var requests = journal
            .Where(record =>
                record.Kind == RuntimeJournalKind.InteractionRequested
                && record.Payload is not null
                && record.Identity is not null
            )
            .ToDictionary(record => record.Identity!, StringComparer.Ordinal);
        foreach (
            var answer in journal.Where(record =>
                record.Kind == RuntimeJournalKind.InteractionAnswered
                && record.Payload is not null
                && record.Identity is not null
            )
        )
        {
            if (requests.TryGetValue(answer.Identity!, out var request))
            {
                yield return new HumanAnswerRecord(
                    answer.Identity!,
                    answer.StepId,
                    Deserialize<HumanQuestion>(request),
                    Deserialize<HumanAnswer>(answer)
                );
            }
        }
    }

    private static T Deserialize<T>(RuntimeJournalRecord record)
        where T : class
    {
        if (record.Payload is not { } payload)
        {
            throw new InvalidOperationException(
                $"Runtime journal record '{record.Identity}' has no '{typeof(T).Name}' payload."
            );
        }
        return payload.Deserialize<T>(JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException(
                $"Runtime journal record '{record.Identity}' contains an invalid '{typeof(T).Name}' payload."
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

    public async ValueTask AcceptCheckpointAsync(
        string acceptedCallId,
        ProgressCheckpointRecord checkpoint,
        CancellationToken cancellationToken
    ) => await ledger.AppendAsync(_checkpoints, acceptedCallId, checkpoint, cancellationToken);

    public async ValueTask AcceptPublicationCandidateAsync(
        string acceptedCandidateId,
        PublicationCandidateDocument candidate,
        CancellationToken cancellationToken
    ) =>
        await WriteAcceptedDocumentAsync(
            PublicationCandidate,
            candidate,
            acceptedCandidateId,
            document => document.AcceptedCandidateId,
            cancellationToken
        );

    public async ValueTask<PublicationCandidateDocument?> ReadPublicationCandidateAsync(
        CancellationToken cancellationToken
    ) => (await ledger.ReadDocumentAsync(PublicationCandidate, cancellationToken))?.Value;

    public async ValueTask AcceptPublicationResultAsync(
        PublicationResultRecord result,
        CancellationToken cancellationToken
    ) =>
        await ledger.AppendAsync(
            PublicationResults,
            $"publication--{result.Branch}--{result.CandidateSha}",
            result,
            cancellationToken
        );

    public async ValueTask AcceptVerificationResultAsync(
        string acceptedResultId,
        VerificationResult result,
        CancellationToken cancellationToken
    ) =>
        await ledger.AppendAsync(
            VerificationResults,
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
