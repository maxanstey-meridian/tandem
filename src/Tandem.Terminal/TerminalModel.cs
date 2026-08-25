namespace Tandem.Terminal;

internal enum TranscriptKind
{
    Text,
    Reasoning,
    ToolStarted,
    ToolCompleted,
    Command,
    Action,
    Semantic,
}

internal sealed record TranscriptEntry(
    string StepId,
    TranscriptKind Kind,
    string Text,
    string? ToolName = null,
    bool? Succeeded = null,
    string? WorkingDirectory = null
);

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
    string Draft,
    string? Title,
    string? WorkingDirectory
);

internal sealed class TerminalModel(
    string pipelineName,
    Guid runId,
    TimeProvider timeProvider,
    int entryCapacity,
    int characterCapacity,
    string? title,
    string? workingDirectory,
    IReadOnlySet<string>? truncatedToolNames = null
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
    private int? _contextWindowTokens;
    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);
    private TerminalInteractionPrompt? _interaction;
    private string _draft = "";
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();
    private DateTimeOffset? _completedAt;
    private string? _modelName;
    private readonly Dictionary<string, string> _models = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeSteps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _usageOrder = new(StringComparer.Ordinal);
    private long _usageSequence;
    private readonly Dictionary<string, (long Current, int Window)> _usage = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _truncatedToolNames = new(
        truncatedToolNames ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal),
        StringComparer.Ordinal
    );

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
                    _activeSteps.Add(started.StepId);
                    _activeStep = started.StepId;
                    _modelName = _models.GetValueOrDefault(started.StepId);
                    ApplyUsage(started.StepId);
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
                case PipelineAgentUpdated { Update: AgentUpdate.ModelSelected selected } update:
                    _activeStep = update.StepId;
                    _models[update.StepId] = selected.ModelId;
                    _modelName = selected.ModelId;
                    break;
                case PipelineAgentUpdated { Update: AgentUpdate.ToolStarted tool } update:
                    _toolNames[tool.CallId] = tool.Name;
                    Append(
                        update.StepId,
                        TranscriptKind.ToolStarted,
                        DisplayArguments(tool, _truncatedToolNames),
                        tool.Name,
                        workingDirectory: tool.WorkingDirectory
                    );
                    break;
                case PipelineAgentUpdated { Update: AgentUpdate.ToolCompleted tool } update:
                    _toolNames.Remove(tool.CallId, out var toolName);
                    Append(
                        update.StepId,
                        TranscriptKind.ToolCompleted,
                        tool.Error ?? tool.Result ?? toolName ?? tool.CallId,
                        toolName,
                        tool.Succeeded
                    );
                    break;
                case PipelineCommandOutput command:
                    Append(
                        command.StepId,
                        TranscriptKind.Command,
                        $"{command.Command}\n{command.Output}",
                        succeeded: command.ExitCode == 0
                    );
                    break;
                case PipelineActionCompleted action when action.Result != "Completed":
                    Append(
                        action.StepId,
                        TranscriptKind.Action,
                        $"{action.ActionName}: {action.Result}",
                        succeeded: false
                    );
                    break;
                case PipelineCapabilityAccepted accepted:
                    Append(accepted.StepId, TranscriptKind.Text, accepted.Summary);
                    if (accepted.Payload is { } capabilityPayload)
                    {
                        AppendSemantic(accepted.StepId, capabilityPayload);
                    }
                    break;
                case PipelineStructuredOutputAccepted { Payload: { } payload } accepted:
                    AppendSemantic(accepted.StepId, payload);
                    break;
                case PipelineStructuredOutputRejected rejected:
                    Append(
                        rejected.StepId,
                        TranscriptKind.ToolCompleted,
                        System.Text.Json.JsonSerializer.Serialize(
                            new
                            {
                                isError = true,
                                error = "structured output rejected",
                                problems = rejected.Problems,
                            }
                        ),
                        succeeded: false
                    );
                    break;
                case PipelineAgentUsage usage:
                    _inputTokens += usage.InputTokens;
                    _outputTokens += usage.OutputTokens;
                    _usage[usage.StepId] = (usage.CurrentContextTokens, usage.ContextWindowTokens);
                    _usageOrder[usage.StepId] = ++_usageSequence;
                    if (_activeSteps.Contains(usage.StepId))
                    {
                        _activeStep = usage.StepId;
                        _modelName = _models.GetValueOrDefault(usage.StepId);
                        ApplyUsage(usage.StepId);
                    }
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

    private static string Json(System.Text.Json.JsonElement value) =>
        value.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "{}" : value.GetRawText();

    internal static string DisplayArguments(
        AgentUpdate.ToolStarted tool,
        IReadOnlySet<string> truncatedToolNames
    ) => truncatedToolNames.Contains(tool.Name) ? "" : Json(tool.Arguments);

    private void AppendSemantic(string stepId, System.Text.Json.JsonElement value)
    {
        var json = Json(value);
        if (
            _transcript.LastOrDefault(entry =>
                entry.StepId == stepId && entry.Kind == TranscriptKind.Semantic
            )
                is { } semantic
            && JsonEquals(semantic.Text, value)
        )
        {
            return;
        }
        if (
            _transcript.LastOrDefault() is { Kind: TranscriptKind.Text } last
            && last.StepId == stepId
            && JsonEquals(last.Text, value)
        )
        {
            _transcript[^1] = last with { Kind = TranscriptKind.Semantic, Text = json };
            _characters += json.Length - last.Text.Length;
            return;
        }
        Append(stepId, TranscriptKind.Semantic, json);
    }

    private static bool JsonEquals(string text, System.Text.Json.JsonElement value)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return System.Text.Json.JsonElement.DeepEquals(document.RootElement, value);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
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
                _contextWindowTokens,
                _waiting.Count,
                _interaction,
                _draft,
                title,
                workingDirectory
            );
        }
    }

    private void ApplyUsage(string stepId)
    {
        var usage = _usage.GetValueOrDefault(stepId);
        _currentContextTokens = usage.Current;
        _contextWindowTokens = usage.Window > 0 ? usage.Window : null;
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
            _activeSteps.Remove(stepId);
            _activeStep = _activeSteps
                .OrderByDescending(active => _usageOrder.GetValueOrDefault(active))
                .FirstOrDefault();
            _modelName = _activeStep is null ? null : _models.GetValueOrDefault(_activeStep);
            if (_activeStep is null)
            {
                _currentContextTokens = 0;
                _contextWindowTokens = null;
            }
            else
            {
                ApplyUsage(_activeStep);
            }
        }
        else
        {
            _activeSteps.Remove(stepId);
        }
    }

    private void Append(
        string stepId,
        TranscriptKind kind,
        string text,
        string? toolName = null,
        bool? succeeded = null,
        string? workingDirectory = null
    )
    {
        text = TerminalText.Sanitize(text);
        if (text.Length == 0 && kind != TranscriptKind.ToolStarted)
        {
            return;
        }
        if (
            kind is TranscriptKind.Text or TranscriptKind.Reasoning
            && _transcript.LastOrDefault() is { } last
            && last.StepId == stepId
            && last.Kind == kind
        )
        {
            _transcript[^1] = last with { Text = last.Text + text };
        }
        else
        {
            _transcript.Add(new(stepId, kind, text, toolName, succeeded, workingDirectory));
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
