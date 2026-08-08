namespace Tandem.Infrastructure.Dashboard;

internal sealed class LiveTranscript(int entryCapacity = 2_000, int characterCapacity = 200_000)
    : IPipelineObserver
{
    private readonly object _gate = new();
    private readonly List<TranscriptEntry> _entries = [];
    private int _characters;

    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        if (observation is not PipelineAgentUpdated update)
        {
            return ValueTask.CompletedTask;
        }
        var line = update.Update switch
        {
            AgentUpdate.Text text => new TranscriptLine(
                TranscriptKinds.Text,
                text.Value,
                DateTimeOffset.UtcNow
            ),
            AgentUpdate.Reasoning reasoning => new TranscriptLine(
                TranscriptKinds.Reasoning,
                reasoning.Value,
                DateTimeOffset.UtcNow
            ),
            _ => null,
        };
        if (line is null)
        {
            return ValueTask.CompletedTask;
        }
        lock (_gate)
        {
            if (
                _entries.LastOrDefault() is { } last
                && last.StepId == update.StepId
                && last.Line.Kind == line.Kind
            )
            {
                _entries[^1] = last with { Line = line with { Text = last.Line.Text + line.Text } };
            }
            else
            {
                _entries.Add(new TranscriptEntry(update.StepId, line));
            }
            _characters += line.Text.Length;
            if (_entries.Count > entryCapacity)
            {
                var removed = _entries.Count - entryCapacity;
                _characters -= _entries.Take(removed).Sum(entry => entry.Line.Text.Length);
                _entries.RemoveRange(0, removed);
            }
            while (_characters > characterCapacity)
            {
                var excess = _characters - characterCapacity;
                if (_entries[0].Line.Text.Length <= excess)
                {
                    _characters -= _entries[0].Line.Text.Length;
                    _entries.RemoveAt(0);
                    continue;
                }
                _entries[0] = _entries[0] with
                {
                    Line = _entries[0].Line with { Text = _entries[0].Line.Text[excess..] },
                };
                _characters -= excess;
            }
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<TranscriptEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
