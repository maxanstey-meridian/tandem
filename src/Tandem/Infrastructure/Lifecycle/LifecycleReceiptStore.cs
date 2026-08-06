using System.Text.Json;

namespace Tandem.Infrastructure.Lifecycle;

public sealed record LifecycleReceipt(
    string InvocationId,
    string BlockId,
    string Kind,
    string Summary,
    JsonElement Payload,
    DateTimeOffset AcceptedAt
);

public sealed class LifecycleReceiptStore(string tandemHome)
{
    public async Task<LifecycleReceipt?> ReadAsync(
        Guid runId,
        string invocationId,
        CancellationToken cancellationToken
    )
    {
        var path = ResolvePath(runId, invocationId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var receipt = await JsonSerializer.DeserializeAsync<LifecycleReceipt>(
            stream,
            cancellationToken: cancellationToken
        );
        if (receipt is null)
        {
            return null;
        }

        // JsonElement values from DeserializeAsync hold references to the
        // underlying stream buffer. Clone the payload so it remains valid
        // after the stream is disposed.
        return receipt with
        {
            Payload = CloneJsonElement(receipt.Payload),
        };
    }

    private static JsonElement CloneJsonElement(JsonElement element)
    {
        return JsonSerializer.SerializeToElement(element);
    }

    public async Task<LifecycleReceipt> WriteAsync(
        Guid runId,
        string invocationId,
        string blockId,
        string kind,
        string summary,
        JsonElement payload,
        CancellationToken cancellationToken
    )
    {
        var receipt = new LifecycleReceipt(
            invocationId,
            blockId,
            kind,
            summary,
            payload,
            DateTimeOffset.UtcNow
        );

        var path = ResolvePath(runId, invocationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                receipt,
                cancellationToken: cancellationToken
            );
        }

        File.Move(tempPath, path);
        return receipt;
    }

    private string ResolvePath(Guid runId, string invocationId) =>
        Path.Combine(tandemHome, "runs", runId.ToString("N"), "lifecycle", $"{invocationId}.json");
}
