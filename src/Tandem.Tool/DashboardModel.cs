using System.Collections.ObjectModel;
using System.Text.Json;
using Tandem.Delivery;
using Tandem.Ledger;

namespace Tandem.Infrastructure.Dashboard;

internal static class TranscriptKinds
{
    internal const string Text = "text";
    internal const string Reasoning = "reasoning";
}

public sealed record StepTranscript(
    string StepId,
    bool IsActive,
    bool IsCompleted,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan? Duration,
    string? OutcomeKind,
    string? OutcomeSummary,
    IReadOnlyList<TranscriptLine> Lines
);

public sealed record TranscriptLine(string Kind, string Text, DateTimeOffset Timestamp);

public sealed record TranscriptEntry(string StepId, TranscriptLine Line);

public sealed record PipelineEntry(
    string StepId,
    string Kind,
    string Summary,
    TimeSpan Duration,
    bool IsVerification,
    int? ExitCode,
    bool? IsReview,
    bool? IsHuman
);

public sealed record HumanRequestView(string InteractionId, string Question, string Reason);

public sealed record DashboardModel
{
    public string RunId { get; init; } = "";
    public string PacketPath { get; init; } = "";
    public string RepositoryPath { get; init; } = "";
    public string WorkspacePath { get; init; } = "";
    public RunStatus Status { get; init; } = RunStatus.Running;
    public string? ActiveStepId { get; init; }
    public string? PinnedBaseSha { get; init; }
    public string? CandidateSha { get; init; }
    public string? PublishedBranch { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int? CurrentContextTokens { get; init; }
    public int? ContextWindowTokens { get; init; }
    public IReadOnlyList<StepTranscript> Steps { get; init; } = [];
    public IReadOnlyList<TranscriptEntry> Transcript { get; init; } = [];
    public IReadOnlyList<PipelineEntry> PipelineHistory { get; init; } = [];
    public HumanRequestView? PendingHumanRequest { get; init; }
    public string? DraftAnswer { get; init; }
    public bool IsReady => Status == RunStatus.Ready;
    public bool IsTerminal =>
        Status is RunStatus.Ready or RunStatus.Failed or RunStatus.Faulted or RunStatus.Cancelled;
}

internal static class DashboardReducer
{
    internal static DashboardModel ApplyRun(DashboardModel model, LedgerRun run) =>
        model with
        {
            RunId = run.RunId.ToString("N"),
            Status = run.Status switch
            {
                LedgerRunStatus.Ready => RunStatus.Ready,
                LedgerRunStatus.Failed => RunStatus.Failed,
                LedgerRunStatus.Faulted => RunStatus.Faulted,
                LedgerRunStatus.Cancelled => RunStatus.Cancelled,
                _ => model.PendingHumanRequest is null
                    ? RunStatus.Running
                    : RunStatus.WaitingForHuman,
            },
            StartedAt = run.StartedAt,
            CompletedAt = run.EndedAt,
            UpdatedAt = run.UpdatedAt,
            ActiveStepId = run.Status == LedgerRunStatus.Running ? model.ActiveStepId : null,
            Steps = run.Status == LedgerRunStatus.Running ? model.Steps : Deactivate(model.Steps),
        };

    internal static DashboardModel ApplyJournal(
        DashboardModel model,
        IEnumerable<AcceptedLedgerEntry<RuntimeJournalRecord>> entries
    )
    {
        foreach (var entry in entries)
        {
            var record = entry.Value;
            switch (record.Kind)
            {
                case RuntimeJournalKind.StepStarted:
                    model = model with
                    {
                        ActiveStepId = record.StepId,
                        Steps = Start(model.Steps, record.StepId, entry.RecordedAt),
                        UpdatedAt = entry.RecordedAt,
                    };
                    break;
                case RuntimeJournalKind.StepCompleted:
                case RuntimeJournalKind.StepFaulted:
                case RuntimeJournalKind.StepCancelled:
                    model = Complete(model, record, entry.RecordedAt);
                    break;
                case RuntimeJournalKind.UsageRecorded:
                    model = model with
                    {
                        CurrentContextTokens = checked((int?)record.CurrentContextTokens),
                        UpdatedAt = entry.RecordedAt,
                    };
                    break;
                case RuntimeJournalKind.InteractionRequested:
                    model = model with
                    {
                        PendingHumanRequest = Request(record),
                        Status = RunStatus.WaitingForHuman,
                        PipelineHistory = AddHumanHistory(
                            model.PipelineHistory,
                            record,
                            "requested"
                        ),
                        UpdatedAt = entry.RecordedAt,
                    };
                    break;
                case RuntimeJournalKind.InteractionAnswered:
                    model = model with
                    {
                        PendingHumanRequest = null,
                        DraftAnswer = null,
                        Status = RunStatus.Running,
                        PipelineHistory = AddHumanHistory(
                            model.PipelineHistory,
                            record,
                            "answered"
                        ),
                        UpdatedAt = entry.RecordedAt,
                    };
                    break;
            }
        }
        return model;
    }

    internal static DashboardModel ApplyDelivery(
        DashboardModel model,
        PublicationCandidateDocument? candidate,
        IReadOnlyList<AcceptedLedgerEntry<VerificationResultRecord>> verification,
        IReadOnlyList<AcceptedLedgerEntry<PublicationResultRecord>> publications
    )
    {
        var history = model.PipelineHistory.Where(entry => !entry.IsVerification).ToList();
        history.AddRange(
            verification.Select(entry => new PipelineEntry(
                "verify",
                entry.Value.Result.ExitCode == 0 ? "passed" : "failed",
                entry.Value.Result.Command,
                entry.Value.Result.Elapsed,
                true,
                entry.Value.Result.ExitCode,
                false,
                false
            ))
        );
        return model with
        {
            RepositoryPath = candidate?.Repository ?? model.RepositoryPath,
            WorkspacePath = candidate?.WorkspacePath ?? model.WorkspacePath,
            PinnedBaseSha = candidate?.PinnedBaseSha ?? model.PinnedBaseSha,
            CandidateSha = candidate?.CandidateSha ?? model.CandidateSha,
            PublishedBranch = publications.LastOrDefault()?.Value.Branch,
            PipelineHistory = new ReadOnlyCollection<PipelineEntry>(history),
        };
    }

    internal static DashboardModel ApplyTranscript(
        DashboardModel model,
        IReadOnlyList<TranscriptEntry> transcript
    )
    {
        var steps = model.Steps.ToList();
        foreach (var group in transcript.GroupBy(entry => entry.StepId))
        {
            var index = steps.FindIndex(step => step.StepId == group.Key);
            if (index < 0)
            {
                steps.Add(
                    new StepTranscript(
                        group.Key,
                        false,
                        false,
                        null,
                        null,
                        null,
                        null,
                        null,
                        group.Select(x => x.Line).ToArray()
                    )
                );
            }
            else
            {
                steps[index] = steps[index] with { Lines = group.Select(x => x.Line).ToArray() };
            }
        }
        return model with { Transcript = transcript, Steps = steps };
    }

    private static DashboardModel Complete(
        DashboardModel model,
        RuntimeJournalRecord record,
        DateTimeOffset at
    )
    {
        var steps = model.Steps.ToList();
        var index = steps.FindIndex(step => step.StepId == record.StepId);
        var started = index >= 0 ? steps[index].StartedAt : null;
        var step = new StepTranscript(
            record.StepId,
            false,
            true,
            started,
            at,
            started is null ? null : at - started,
            record.OutcomeKind,
            record.Result,
            index >= 0 ? steps[index].Lines : []
        );
        if (index >= 0)
        {
            steps[index] = step;
        }
        else
        {
            steps.Add(step);
        }
        var history = model.PipelineHistory.ToList();
        history.Add(
            new PipelineEntry(
                record.StepId,
                record.OutcomeKind ?? record.Kind.ToString(),
                record.Result ?? "",
                step.Duration ?? TimeSpan.Zero,
                false,
                null,
                record.StepId.Contains("review", StringComparison.OrdinalIgnoreCase),
                record.StepId.Contains("human", StringComparison.OrdinalIgnoreCase)
            )
        );
        return model with { Steps = steps, PipelineHistory = history, UpdatedAt = at };
    }

    private static IReadOnlyList<StepTranscript> Start(
        IReadOnlyList<StepTranscript> current,
        string step,
        DateTimeOffset at
    )
    {
        var steps = current.Select(item => item with { IsActive = item.StepId == step }).ToList();
        var index = steps.FindIndex(item => item.StepId == step);
        if (index < 0)
        {
            steps.Add(new StepTranscript(step, true, false, at, null, null, null, null, []));
        }
        else
        {
            steps[index] = steps[index] with
            {
                IsActive = true,
                IsCompleted = false,
                StartedAt = at,
            };
        }
        return steps;
    }

    private static IReadOnlyList<StepTranscript> Deactivate(IReadOnlyList<StepTranscript> steps) =>
        steps.Select(step => step with { IsActive = false }).ToArray();

    private static HumanRequestView Request(RuntimeJournalRecord record)
    {
        var question = record.Payload?.Deserialize<HumanQuestion>(JsonSerializerOptions.Web);
        return new HumanRequestView(
            record.Identity ?? record.StepId,
            question?.Question ?? "",
            question?.Reason ?? ""
        );
    }

    private static IReadOnlyList<PipelineEntry> AddHumanHistory(
        IReadOnlyList<PipelineEntry> current,
        RuntimeJournalRecord record,
        string kind
    ) =>
        [
            .. current,
            new PipelineEntry(
                record.StepId,
                kind,
                record.Name ?? record.Result ?? "",
                TimeSpan.Zero,
                false,
                null,
                false,
                true
            ),
        ];
}
