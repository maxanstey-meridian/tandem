using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Domain;

namespace Tandem.Infrastructure.Projection;

public sealed class EventStore(string runDirectory)
{
    private static readonly System.Text.UTF8Encoding _utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path = Path.Combine(runDirectory, "events.jsonl");
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task AppendAsync(RunEvent evt, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(evt, _jsonOptions);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var writer = new StreamWriter(_path, append: true, _utf8WithoutBom);
            await writer.WriteLineAsync(line);
            await writer.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<RunEvent> AppendProjectedAsync(
        Guid runId,
        string blockId,
        string kind,
        Func<string, RunEvent> createEvent,
        CancellationToken ct = default
    )
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var prefix = $"{runId:N}--{blockId}--{kind}--";
            var sequence = 0;
            if (File.Exists(_path))
            {
                foreach (var line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var existing = JsonSerializer.Deserialize<RunEvent>(line, _jsonOptions);
                    if (
                        existing is not null
                        && existing.EventId.StartsWith(prefix, StringComparison.Ordinal)
                        && int.TryParse(existing.EventId.AsSpan(prefix.Length), out var candidate)
                    )
                    {
                        sequence = Math.Max(sequence, candidate);
                    }
                }
            }

            var evt = createEvent($"{prefix}{sequence + 1}");
            var serialized = JsonSerializer.Serialize(evt, _jsonOptions);
            await using var writer = new StreamWriter(_path, append: true, _utf8WithoutBom);
            await writer.WriteLineAsync(serialized);
            await writer.FlushAsync(ct);
            return evt;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task AppendRangeAsync(
        IReadOnlyList<RunEvent> events,
        CancellationToken ct = default
    )
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var writer = new StreamWriter(_path, append: true, _utf8WithoutBom);
            foreach (var evt in events)
            {
                var line = JsonSerializer.Serialize(evt, _jsonOptions);
                await writer.WriteLineAsync(line);
            }
            await writer.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<RunEvent> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var seen = new HashSet<string>();
        var events = new List<RunEvent>();

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize<RunEvent>(line, _jsonOptions);
            if (evt is null)
            {
                continue;
            }

            if (seen.Add(evt.EventId))
            {
                events.Add(evt);
            }
        }

        return events;
    }

    public async Task<IReadOnlyList<RunEvent>> ReadAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var seen = new HashSet<string>();
        var events = new List<RunEvent>();

        await foreach (var line in File.ReadLinesAsync(_path, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize<RunEvent>(line, _jsonOptions);
            if (evt is null)
            {
                continue;
            }

            if (seen.Add(evt.EventId))
            {
                events.Add(evt);
            }
        }

        return events;
    }
}
