using Tandem.Domain;

namespace Tandem.Infrastructure.Dashboard;

public enum DashboardOutcome
{
    Detached,
    AnswerSubmitted,
    PublishRequested,
}

public sealed class DashboardLoop(
    string runDirectory,
    Func<string?, Task> onAnswerSubmitted,
    Func<Task> onPublishRequested,
    Func<Task> onDetach,
    DashboardRenderer? renderer = null
)
{
    private readonly DashboardEventFeed _feed = new(runDirectory);
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

        DashboardOutcome outcome = DashboardOutcome.Detached;
        var previousControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            _renderer.RunInAlternateScreen(() =>
                outcome = RunCoreAsync(seed, ct).GetAwaiter().GetResult()
            );
        }
        finally
        {
            Console.TreatControlCAsInput = previousControlC;
        }

        return Task.FromResult(outcome);
    }

    private async Task<DashboardOutcome> RunCoreAsync(DashboardModel? seed, CancellationToken ct)
    {
        DashboardModel model = seed ?? new DashboardModel();

        var existing = await _feed.ReadExistingAsync(ct);
        model = DashboardReducer.FromEvents(existing, model);
        _renderer.Render(model);
        var lastRender = DateTimeOffset.UtcNow;
        var width = _renderer.Width;
        var height = _renderer.Height;

        while (!ct.IsCancellationRequested)
        {
            var fresh = await _feed.PollNewAsync(ct);
            if (fresh.Count > 0)
            {
                model = DashboardReducer.FromEvents(fresh, model);
                _renderer.Render(model);
                lastRender = DateTimeOffset.UtcNow;
            }

            var resized = width != _renderer.Width || height != _renderer.Height;
            if (resized || DateTimeOffset.UtcNow - lastRender >= TimeSpan.FromSeconds(1))
            {
                width = _renderer.Width;
                height = _renderer.Height;
                _renderer.Render(model);
                lastRender = DateTimeOffset.UtcNow;
            }

            if (model.IsTerminal && Console.IsInputRedirected)
            {
                await onDetach();
                return DashboardOutcome.Detached;
            }

            if (Console.IsInputRedirected)
            {
                await Task.Delay(100, ct);
                continue;
            }

            if (!Console.KeyAvailable)
            {
                await Task.Delay(100, ct);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Q:
                case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    await onDetach();
                    return DashboardOutcome.Detached;
                case ConsoleKey.Enter when model.PendingHumanRequest is not null:
                    _renderer.Render(model);
                    await onAnswerSubmitted(model.DraftAnswer);
                    model = model with { DraftAnswer = null };
                    break;
                case ConsoleKey.P when model.IsReady:
                    await onPublishRequested();
                    break;
                default:
                    if (model.PendingHumanRequest is not null && key.Key != ConsoleKey.Enter)
                    {
                        model = AppendAnswerChar(model, key);
                        _renderer.Render(model);
                    }
                    break;
            }
        }

        await onDetach();
        return DashboardOutcome.Detached;
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
}
