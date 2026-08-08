using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Delivery;

namespace Tandem.Infrastructure.Projection;

public sealed class RunProjectionStore(string runDirectory)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path = Path.Combine(runDirectory, "run.json");
    private readonly string _tempPath = Path.Combine(runDirectory, "run.json.tmp");

    public async Task WriteAsync(RunProjection projection, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(projection, _jsonOptions);
        await File.WriteAllTextAsync(_tempPath, json, ct);
        File.Move(_tempPath, _path, overwrite: true);
    }

    public RunProjection? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<RunProjection>(json, _jsonOptions);
    }

    public async Task<RunProjection?> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(_path, ct);
        return JsonSerializer.Deserialize<RunProjection>(json, _jsonOptions);
    }
}
