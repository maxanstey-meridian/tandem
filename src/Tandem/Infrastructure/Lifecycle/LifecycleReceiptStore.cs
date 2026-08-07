using System.Text.Json;

namespace Tandem.Actions;

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
        var result = await CreateOrReadAsync(
            runId,
            invocationId,
            blockId,
            kind,
            summary,
            payload,
            cancellationToken
        );
        if (!result.Created)
        {
            throw new IOException(
                $"A lifecycle receipt already exists for invocation '{invocationId}'."
            );
        }

        return result.Receipt;
    }

    public async Task<LifecycleReceiptWriteResult> CreateOrReadAsync(
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
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var publicationLockPath = path + ".lock";

        try
        {
            await using (
                var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None
                )
            )
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    receipt,
                    cancellationToken: cancellationToken
                );
                await stream.FlushAsync(cancellationToken);
            }

            while (true)
            {
                FileStream? publicationLock = null;
                try
                {
                    publicationLock = new FileStream(
                        publicationLockPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    );
                }
                catch (IOException)
                {
                    var accepted = await ReadAsync(runId, invocationId, cancellationToken);
                    if (accepted is not null)
                    {
                        return new LifecycleReceiptWriteResult(accepted, Created: false);
                    }

                    TryDeleteStaleLock(publicationLockPath);
                    await Task.Delay(10, cancellationToken);
                    continue;
                }

                LifecycleReceiptWriteResult result;
                try
                {
                    await using (publicationLock)
                    {
                        var accepted = await ReadAsync(runId, invocationId, cancellationToken);
                        if (accepted is not null)
                        {
                            result = new LifecycleReceiptWriteResult(accepted, Created: false);
                        }
                        else
                        {
                            File.Move(tempPath, path, overwrite: false);
                            result = new LifecycleReceiptWriteResult(receipt, Created: true);
                        }
                    }
                }
                finally
                {
                    File.Delete(publicationLockPath);
                }

                return result;
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void TryDeleteStaleLock(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(path);
        }
        catch (IOException) { }
    }

    private string ResolvePath(Guid runId, string invocationId) =>
        Path.Combine(tandemHome, "runs", runId.ToString("N"), "lifecycle", $"{invocationId}.json");
}

public sealed record LifecycleReceiptWriteResult(LifecycleReceipt Receipt, bool Created);
