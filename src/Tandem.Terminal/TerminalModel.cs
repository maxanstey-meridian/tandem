namespace Tandem.Terminal;

internal enum TranscriptKind
{
    Text,
    Reasoning,
}

internal sealed record TranscriptEntry(string StepId, TranscriptKind Kind, string Text);

internal sealed record StepVisit(
    string StepId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    string? Outcome = null,
    string? Summary = null,
    TimeSpan? Duration = null
);

internal sealed record TerminalSnapshot(
    string PipelineName,
    Guid RunId,
    TerminalPipelineStatus Status,
    string? Summary,
    string? ActiveStep,
    string? ModelName,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<StepVisit> Visits,
    IReadOnlyList<TranscriptEntry> Transcript,
    long InputTokens,
    long OutputTokens,
    long CurrentContextTokens,
    int? ContextWindowTokens,
    int WaitingInteractions,
    TerminalInteractionPrompt? Interaction,
    string Draft
);

internal sealed class TerminalModel(
    string pipelineName,
    Guid runId,
    TimeProvider timeProvider,
    int entryCapacity,
    int characterCapacity,
    IReadOnlyDictionary<string, string>? modelNames = null,
    int? contextWindowTokens = null
)
{
    private readonly object _gate = new();
    private readonly List<StepVisit> _visits = [];
    private readonly List<TranscriptEntry> _transcript = [];
    private int _characters;
    private string? _activeStep;
    private string? _summary;
    private TerminalPipelineStatus _status = TerminalPipelineStatus.Running;
    private long _inputTokens;
    private long _outputTokens;
    private long _currentContextTokens;
    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);
    private TerminalInteractionPrompt? _interaction;
    private string _draft = "";
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();
    private DateTimeOffset? _completedAt;
    private string? _modelName;

    public void Apply(PipelineObservation observation)
    {
        if (observation.RunId != runId)
        {
            throw new InvalidOperationException(
                $"Terminal for run '{runId:N}' cannot observe run '{observation.RunId:N}'."
            );
        }
        lock (_gate)
        {
            switch (observation)
            {
                case PipelineStepStarted started:
                    _activeStep = started.StepId;
                    if (modelNames?.TryGetValue(started.StepId, out var activeModel) is true)
                    {
                        _modelName = activeModel;
                    }
                    _visits.Add(new(started.StepId, timeProvider.GetUtcNow()));
                    break;
                case PipelineStepCompleted completed:
                    Complete(
                        completed.StepId,
                        completed.Outcome.Kind,
                        completed.Outcome.Summary,
                        completed.Outcome.Duration
                    );
                    break;
                case PipelineStepFaulted faulted:
                    Complete(faulted.StepId, "faulted", faulted.Error, null);
                    break;
                case PipelineStepCancelled cancelled:
                    Complete(cancelled.StepId, "cancelled", null, null);
                    break;
                case PipelineAgentUpdated { Update: AgentUpdate.Text text } update:
                    Append(update.StepId, TranscriptKind.Text, text.Value);
                    break;
                case PipelineAgentUpdated { Update: AgentUpdate.Reasoning reasoning } update:
                    Append(update.StepId, TranscriptKind.Reasoning, reasoning.Value);
                    break;
                case PipelineCapabilityAccepted accepted:
                    Append(accepted.StepId, TranscriptKind.Text, accepted.Summary);
                    break;
                case PipelineAgentUsage usage:
                    _inputTokens += usage.InputTokens;
                    _outputTokens += usage.OutputTokens;
                    _currentContextTokens = usage.CurrentContextTokens;
                    break;
                case PipelineInteractionRequestedObservation requested:
                    _waiting.Add(requested.RequestId);
                    _status = TerminalPipelineStatus.WaitingForInteraction;
                    break;
                case PipelineInteractionAnsweredObservation answered:
                    _waiting.Remove(answered.RequestId);
                    if (_waiting.Count == 0)
                    {
                        _status = TerminalPipelineStatus.Running;
                        _interaction = null;
                        _draft = "";
                    }
                    break;
            }
        }
    }

    public void SetInteraction(TerminalInteractionPrompt? interaction)
    {
        lock (_gate)
        {
            _interaction = interaction;
        }
    }

    public void AppendDraft(ConsoleKeyInfo key)
    {
        lock (_gate)
        {
            if (key.Key == ConsoleKey.Backspace && _draft.Length > 0)
            {
                _draft = _draft[..^1];
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _draft += key.KeyChar;
            }
        }
    }

    public string TakeDraft()
    {
        lock (_gate)
        {
            var draft = _draft;
            _draft = "";
            return draft;
        }
    }

    public void Finish(TerminalPipelineStatus status, string summary)
    {
        lock (_gate)
        {
            _status = status;
            _summary = summary;
            _activeStep = null;
            _completedAt = timeProvider.GetUtcNow();
        }
    }

    public TerminalSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(
                pipelineName,
                runId,
                _status,
                _summary,
                _activeStep,
                _modelName,
                _startedAt,
                _completedAt,
                _visits.ToArray(),
                _transcript.ToArray(),
                _inputTokens,
                _outputTokens,
                _currentContextTokens,
                contextWindowTokens,
                _waiting.Count,
                _interaction,
                _draft
            );
        }
    }

    private void Complete(string stepId, string outcome, string? summary, TimeSpan? duration)
    {
        var index = _visits.FindLastIndex(visit =>
            visit.StepId == stepId && visit.CompletedAt is null
        );
        var completedAt = timeProvider.GetUtcNow();
        if (index < 0)
        {
            _visits.Add(new(stepId, completedAt, completedAt, outcome, summary, duration));
        }
        else
        {
            var visit = _visits[index];
            _visits[index] = visit with
            {
                CompletedAt = completedAt,
                Outcome = outcome,
                Summary = summary,
                Duration = duration ?? completedAt - visit.StartedAt,
            };
        }
        if (_activeStep == stepId)
        {
            _activeStep = null;
        }
    }

    private void Append(string stepId, TranscriptKind kind, string text)
    {
        text = TerminalText.Sanitize(text);
        if (text.Length == 0)
        {
            return;
        }
        if (_transcript.LastOrDefault() is { } last && last.StepId == stepId && last.Kind == kind)
        {
            _transcript[^1] = last with { Text = last.Text + text };
        }
        else
        {
            _transcript.Add(new(stepId, kind, text));
        }
        _characters += text.Length;
        while (_transcript.Count > entryCapacity)
        {
            _characters -= _transcript[0].Text.Length;
            _transcript.RemoveAt(0);
        }
        while (_characters > characterCapacity && _transcript.Count > 0)
        {
            var excess = _characters - characterCapacity;
            if (_transcript[0].Text.Length <= excess)
            {
                _characters -= _transcript[0].Text.Length;
                _transcript.RemoveAt(0);
            }
            else
            {
                _transcript[0] = _transcript[0] with { Text = _transcript[0].Text[excess..] };
                _characters -= excess;
            }
        }
    }
}
