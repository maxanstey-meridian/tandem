using Spectre.Console;

namespace Tandem.Terminal;

public sealed class TerminalPipelineDisplay : IAsyncDisposable
{
    private static int _interactiveOwner;
    private readonly TerminalDisplayOptions _options;
    private readonly IReadOnlySet<string> _truncatedToolNames;
    private readonly IAnsiConsole _console;
    private readonly TerminalModel _model;
    private readonly TerminalRenderer _renderer;
    private readonly TaskCompletionSource _finished = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly DisplayObserver _observer;
    private readonly ITerminalKeyInput? _keyInput;
    private IReadOnlyList<TerminalPipelineEntry> _pipelineEntries = [];
    private Task? _displayTask;
    private int _started;
    private int _terminalized;
    private int _cancelRequested;
    private bool _ownsInteractiveTerminal;
    private Exception? _displayFailure;

    public TerminalPipelineDisplay(
        PipelineInspection pipeline,
        Guid runId,
        TerminalDisplayOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _options = options ?? new TerminalDisplayOptions();
        if (
            _options.TranscriptEntryCapacity <= 0
            || _options.TranscriptCharacterCapacity <= 0
            || _options.RefreshInterval <= TimeSpan.Zero
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Capacities and refresh interval must be positive."
            );
        }
        _truncatedToolNames = new HashSet<string>(
            _options.TruncatedToolNames,
            StringComparer.Ordinal
        );
        _console = _options.Console ?? AnsiConsole.Console;
        _model = new(
            pipeline.Name,
            runId,
            _options.TimeProvider,
            _options.TranscriptEntryCapacity,
            _options.TranscriptCharacterCapacity,
            _options.Title,
            _options.WorkingDirectory,
            _truncatedToolNames
        );
        _renderer = new(_console, _options.KeyActions, pipeline.StepIds);
        _observer = new(this);
        _keyInput =
            _options.KeyInput
            ?? (_options.Capabilities.IsInteractive ? new SystemTerminalKeyInput() : null);
    }

    public IPipelineObserver Observer => _observer;

    public bool IsInteractive => _options.Capabilities.IsInteractive;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The terminal display has already been started.");
        }
        if (IsInteractive)
        {
            if (Interlocked.CompareExchange(ref _interactiveOwner, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Another interactive terminal presentation is already active in this process."
                );
            }
            _ownsInteractiveTerminal = true;
        }
        _displayTask = RunDisplayAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public ValueTask SucceededAsync(string summary) =>
        FinishAsync(TerminalPipelineStatus.Succeeded, summary);

    public ValueTask FailedAsync(string summary) =>
        FinishAsync(TerminalPipelineStatus.Failed, summary);

    public ValueTask FaultedAsync(string summary) =>
        FinishAsync(TerminalPipelineStatus.Faulted, summary);

    public ValueTask CancelledAsync(string summary) =>
        FinishAsync(TerminalPipelineStatus.Cancelled, summary);

    public async Task WaitForCleanupAsync(CancellationToken cancellationToken = default)
    {
        var task =
            _displayTask
            ?? throw new InvalidOperationException("The terminal display has not been started.");
        await task.WaitAsync(cancellationToken);
        if (_displayFailure is { } failure)
        {
            throw new InvalidOperationException("Terminal presentation failed.", failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _terminalized) == 0)
        {
            await CancelledAsync("Display disposed");
        }
        _finished.TrySetResult();
        if (_displayTask is not null)
        {
            await _displayTask;
        }
    }

    private ValueTask FinishAsync(TerminalPipelineStatus status, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (Interlocked.Exchange(ref _terminalized, 1) == 0)
        {
            _model.Finish(status, summary);
            if (!IsInteractive && Volatile.Read(ref _started) != 0)
            {
                WritePlain($"pipeline {status}: {summary}");
            }
            if (
                !IsInteractive
                || status is TerminalPipelineStatus.Faulted or TerminalPipelineStatus.Cancelled
            )
            {
                _finished.TrySetResult();
            }
        }
        return ValueTask.CompletedTask;
    }

    private async Task RunDisplayAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (IsInteractive)
            {
                await Task.Run(
                    () =>
                        _console.AlternateScreen(() =>
                            RunInteractiveAsync(cancellationToken).GetAwaiter().GetResult()
                        ),
                    CancellationToken.None
                );
            }
            else
            {
                await RunPlainAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _displayFailure, exception, null);
            try
            {
                await RequestCancellationAsync(CancellationToken.None);
            }
            catch (Exception cancellationFailure)
            {
                Interlocked.CompareExchange(ref _displayFailure, cancellationFailure, null);
            }
        }
        finally
        {
            _finished.TrySetResult();
            if (_ownsInteractiveTerminal)
            {
                _ownsInteractiveTerminal = false;
                Interlocked.Exchange(ref _interactiveOwner, 0);
            }
        }
    }

    private async Task RunInteractiveAsync(CancellationToken cancellationToken)
    {
        _console.Write("\x1b[?25l");
        try
        {
            using var registration = cancellationToken.Register(() => _finished.TrySetResult());
            while (!_finished.Task.IsCompleted)
            {
                if (_options.ReadPipelineEntriesAsync is { } readPipelineEntries)
                {
                    _pipelineEntries = await readPipelineEntries(cancellationToken);
                }
                _renderer.Render(_model.Snapshot(), _pipelineEntries);
                await ReadKeysAsync(cancellationToken);
                await Task.WhenAny(
                    _finished.Task,
                    Task.Delay(_options.RefreshInterval, _options.TimeProvider, cancellationToken)
                );
            }
            _renderer.Render(_model.Snapshot(), _pipelineEntries);
            await _finished.Task;
        }
        finally
        {
            _console.Write("\x1b[?25h");
        }
    }

    private async Task RunPlainAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => _finished.TrySetResult());
        WritePlain(
            $"pipeline {_model.Snapshot().PipelineName} run {_model.Snapshot().RunId:N} started"
        );
        await _finished.Task;
    }

    private async ValueTask ReadKeysAsync(CancellationToken cancellationToken)
    {
        if (_keyInput is null)
        {
            return;
        }

        var keys = new List<ConsoleKeyInfo>();
        while (await _keyInput.ReadAsync(cancellationToken) is { } key)
        {
            keys.Add(key);
        }
        var interactionActive = _model.Snapshot().Interaction is not null;
        var quitIndex = keys.FindIndex(key =>
            key.Key == ConsoleKey.Q && !interactionActive
            || key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0
        );
        if (quitIndex >= 0)
        {
            foreach (var key in keys.Take(quitIndex).Where(key => !IsScrollKey(key.Key)))
            {
                await HandleKeyAsync(key, cancellationToken);
            }
            await RequestCancellationAsync(cancellationToken);
            return;
        }

        foreach (var key in keys)
        {
            await HandleKeyAsync(key, cancellationToken);
        }
    }

    private static bool IsScrollKey(ConsoleKey key) =>
        key
            is ConsoleKey.UpArrow
                or ConsoleKey.DownArrow
                or ConsoleKey.PageUp
                or ConsoleKey.PageDown
                or ConsoleKey.Home
                or ConsoleKey.End;

    private async ValueTask HandleKeyAsync(ConsoleKeyInfo key, CancellationToken cancellationToken)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _renderer.ScrollLines(1);
                break;
            case ConsoleKey.PageUp:
                _renderer.ScrollPage(1);
                break;
            case ConsoleKey.DownArrow:
                _renderer.ScrollLines(-1);
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
            case ConsoleKey.Enter
                when _model.Snapshot().Interaction is not null
                    && (_options.CanSubmitText?.Invoke() ?? true)
                    && _options.SubmitTextAsync is { } submit:
                var draft = _model.TakeDraft();
                if (!string.IsNullOrWhiteSpace(draft))
                {
                    await submit(draft, cancellationToken);
                }
                break;
            default:
                var action = _options.KeyActions.FirstOrDefault(candidate =>
                    candidate.Key == key.Key && (candidate.IsAvailable?.Invoke() ?? true)
                );
                if (action is not null)
                {
                    await action.ExecuteAsync(cancellationToken);
                }
                else if (_model.Snapshot().Interaction is not null)
                {
                    _model.AppendDraft(key);
                }
                break;
        }
    }

    private async ValueTask RequestCancellationAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _terminalized) != 0)
        {
            _finished.TrySetResult();
            return;
        }
        if (
            Interlocked.Exchange(ref _cancelRequested, 1) == 0
            && _options.CancelAsync is { } cancel
        )
        {
            await cancel(cancellationToken);
        }
        _finished.TrySetResult();
    }

    private void Observe(PipelineObservation observation)
    {
        _model.Apply(observation);
        if (observation is PipelineInteractionRequestedObservation interactionRequest)
        {
            _model.SetInteraction(_options.FormatInteraction?.Invoke(interactionRequest));
        }
        if (!IsInteractive && Volatile.Read(ref _started) != 0)
        {
            switch (observation)
            {
                case PipelineStepStarted started:
                    WritePlain($"{started.StepId} started");
                    break;
                case PipelineAgentUpdated { Update: AgentUpdate.Text text } update:
                    WritePlain($"{update.StepId} text: {text.Value}");
                    break;
                case PipelineAgentUpdated { Update: AgentUpdate.Reasoning reasoning } update:
                    WritePlain($"{update.StepId} reasoning: {reasoning.Value}");
                    break;
                case PipelineAgentUpdated { Update: AgentUpdate.ToolStarted tool } update:
                    var arguments = TerminalModel.DisplayArguments(tool, _truncatedToolNames);
                    WritePlain(
                        $"{update.StepId} tool {ToolStartFormatter.Format(tool.Name, arguments, tool.WorkingDirectory)} started"
                    );
                    break;
                case PipelineAgentUpdated { Update: AgentUpdate.ToolCompleted tool } update:
                    WritePlain(
                        $"{update.StepId} tool {tool.CallId} {(tool.Succeeded ? "completed" : "failed")}"
                    );
                    break;
                case PipelineCommandOutput command:
                    WritePlain(
                        $"{command.StepId} command {command.Command} exited {command.ExitCode}: {command.Output}"
                    );
                    break;
                case PipelineActionAttempted action:
                    WritePlain(
                        $"{action.StepId} action {action.ActionName} ({action.Effect}) started"
                    );
                    break;
                case PipelineActionCompleted action:
                    WritePlain($"{action.StepId} action {action.ActionName} {action.Result}");
                    break;
                case PipelineStepCompleted completed:
                    WritePlain(
                        $"{completed.StepId} {completed.Outcome.Kind}: {completed.Outcome.Summary} ({completed.Outcome.Duration})"
                    );
                    break;
                case PipelineStepFaulted faulted:
                    WritePlain($"{faulted.StepId} faulted: {faulted.Error}");
                    break;
                case PipelineStepCancelled cancelled:
                    WritePlain($"{cancelled.StepId} cancelled");
                    break;
                case PipelineInteractionRequestedObservation requested:
                    WritePlain($"{requested.StepId} waiting for interaction");
                    break;
                case PipelineInteractionAnsweredObservation answered:
                    WritePlain($"{answered.StepId} interaction answered");
                    break;
                case PipelineAgentUsage usage:
                    var contextWindow =
                        usage.ContextWindowTokens > 0
                            ? $"/{usage.ContextWindowTokens}"
                            : string.Empty;
                    WritePlain(
                        $"{usage.StepId} usage: in {usage.InputTokens} out {usage.OutputTokens} ctx {usage.CurrentContextTokens}{contextWindow}"
                    );
                    break;
            }
        }
    }

    private void WritePlain(string value) => _console.WriteLine(TerminalText.Sanitize(value));

    private sealed class DisplayObserver(TerminalPipelineDisplay owner) : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ObserveCoreAsync(observation);
        }

        private async ValueTask ObserveCoreAsync(PipelineObservation observation)
        {
            try
            {
                owner.Observe(observation);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref owner._displayFailure, exception, null);
                try
                {
                    await owner.RequestCancellationAsync(CancellationToken.None);
                }
                catch (Exception cancellationFailure)
                {
                    Interlocked.CompareExchange(
                        ref owner._displayFailure,
                        cancellationFailure,
                        null
                    );
                }
            }
        }
    }

    private sealed class SystemTerminalKeyInput : ITerminalKeyInput
    {
        public ValueTask<ConsoleKeyInfo?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                System.Console.KeyAvailable
                    ? (ConsoleKeyInfo?)System.Console.ReadKey(intercept: true)
                    : null
            );
        }
    }
}
