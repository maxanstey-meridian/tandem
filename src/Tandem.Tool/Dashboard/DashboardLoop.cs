using Tandem.Ledger;
using Tandem.Tool;

namespace Tandem.Infrastructure.Dashboard;

public enum DashboardOutcome
{
    Completed,
    Cancelled,
}

internal sealed class DashboardLoop(
    SqliteLedgerStore store,
    Guid runId,
    LiveTranscript liveTranscript,
    Func<bool> canSubmitAnswer,
    Func<string?, Task> onAnswerSubmitted,
    Func<Task> onPublishRequested,
    Func<Task> onCancel,
    DashboardRenderer? renderer = null
)
{
    private readonly DashboardRenderer _renderer = renderer ?? new DashboardRenderer();

    public Task<DashboardOutcome> RunAsync(
        DashboardModel? seed = null,
        CancellationToken ct = default
    )
    {
        if (Console.IsOutputRedirected)
        {
            return RunCoreAsync(seed, ct);
        }
        DashboardOutcome outcome = DashboardOutcome.Cancelled;
        var previous = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            _renderer.RunInAlternateScreen(() =>
                outcome = RunCoreAsync(seed, ct).GetAwaiter().GetResult()
            );
        }
        finally
        {
            Console.TreatControlCAsInput = previous;
        }
        return Task.FromResult(outcome);
    }

    private async Task<DashboardOutcome> RunCoreAsync(DashboardModel? seed, CancellationToken ct)
    {
        var model = seed ?? new DashboardModel();
        var ledger = store.ForRun(runId);
        long journalSequence = 0;
        while (!ct.IsCancellationRequested)
        {
            var journal = await ledger.ReadAfterAsync(PipelineJournal.Stream, journalSequence, ct);
            if (journal.Count > 0)
            {
                journalSequence = journal[^1].Sequence;
            }
            model = DashboardReducer.ApplyJournal(model, journal);
            model = DashboardReducer.ApplyRun(model, await store.GetRunAsync(runId, ct));
            model = DashboardReducer.ApplyDelivery(
                model,
                (await ledger.ReadDocumentAsync(DeliveryLedger.PublicationCandidate, ct))?.Value,
                await ledger.ReadAsync(DeliveryLedger.VerificationResults, ct),
                await ledger.ReadAsync(DeliveryLedger.PublicationResults, ct)
            );
            model = DashboardReducer.ApplyTranscript(model, liveTranscript.Snapshot());
            _renderer.Render(model);

            if (
                ShouldExitAfterTerminal(
                    model.IsTerminal,
                    Console.IsInputRedirected,
                    Console.IsOutputRedirected
                )
            )
            {
                return DashboardOutcome.Completed;
            }
            if (Console.IsInputRedirected || !Console.KeyAvailable)
            {
                await Task.Delay(100, ct);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    _renderer.ScrollLines(1);
                    break;
                case ConsoleKey.DownArrow:
                    _renderer.ScrollLines(-1);
                    break;
                case ConsoleKey.PageUp:
                    _renderer.ScrollPage(1);
                    break;
                case ConsoleKey.PageDown:
                    _renderer.ScrollPage(-1);
                    break;
                case ConsoleKey.Home:
                    _renderer.ScrollHome();
                    break;
                case ConsoleKey.End:
                    _renderer.ScrollEnd();
                    break;
                case ConsoleKey.Q:
                case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    await onCancel();
                    return DashboardOutcome.Cancelled;
                case ConsoleKey.Enter
                    when model.PendingHumanRequest is not null && canSubmitAnswer():
                    await onAnswerSubmitted(model.DraftAnswer);
                    model = model with { DraftAnswer = null };
                    break;
                case ConsoleKey.P when model.IsReady:
                    await onPublishRequested();
                    break;
                default:
                    if (model.PendingHumanRequest is not null)
                    {
                        model = AppendAnswerChar(model, key);
                    }
                    break;
            }
        }
        return DashboardOutcome.Cancelled;
    }

    private static DashboardModel AppendAnswerChar(DashboardModel model, ConsoleKeyInfo key)
    {
        var draft = model.DraftAnswer ?? "";
        if (key.Key == ConsoleKey.Backspace && draft.Length > 0)
        {
            draft = draft[..^1];
        }
        else if (!char.IsControl(key.KeyChar))
        {
            draft += key.KeyChar;
        }
        return model with { DraftAnswer = draft };
    }

    internal static bool ShouldExitAfterTerminal(
        bool isTerminal,
        bool inputRedirected,
        bool outputRedirected
    ) => isTerminal && (inputRedirected || outputRedirected);
}
