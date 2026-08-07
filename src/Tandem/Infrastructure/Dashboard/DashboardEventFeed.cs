using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Domain;

namespace Tandem.Infrastructure.Dashboard;

public sealed class DashboardEventFeed(string runDirectory)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path = Path.Combine(runDirectory, "events.jsonl");
    private readonly HashSet<string> _seen = new();
    private int _position;
    private string _pending = "";

    public IReadOnlyList<RunEvent> ReadExisting()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        return ReadCompleteLines(File.ReadAllText(_path));
    }

    public async Task<IReadOnlyList<RunEvent>> ReadExistingAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        return ReadCompleteLines(await File.ReadAllTextAsync(_path, ct));
    }

    public async Task<IReadOnlyList<RunEvent>> PollNewAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        return ReadCompleteLines(await File.ReadAllTextAsync(_path, ct));
    }

    private IReadOnlyList<RunEvent> ReadCompleteLines(string content)
    {
        if (content.Length < _position)
        {
            _position = 0;
            _pending = "";
            _seen.Clear();
        }

        var chunk = content[_position..];
        _position = content.Length;
        var buffered = _pending + chunk;
        var lastNewline = buffered.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            _pending = buffered;
            return [];
        }

        var complete = buffered[..lastNewline];
        _pending = buffered[(lastNewline + 1)..];
        var events = new List<RunEvent>();

        foreach (var rawLine in complete.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var evt = JsonSerializer.Deserialize<RunEvent>(line, _jsonOptions);
                if (evt is not null && _seen.Add(evt.EventId))
                {
                    events.Add(evt);
                }
            }
            catch (JsonException)
            {
                // Projection events are observational. A malformed historical
                // line must not terminate the live run.
            }
        }

        return events;
    }
}
