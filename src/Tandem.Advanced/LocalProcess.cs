using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Tandem.Advanced;

public sealed record LocalProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    int MaximumOutputBytesPerStream = 64 * 1024,
    IReadOnlyDictionary<string, string>? Environment = null
);

public sealed record LocalProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut,
    bool StdoutTruncated,
    bool StderrTruncated
);

public static class LocalProcess
{
    private static readonly TimeSpan _terminationTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumAllowedOutputBytesPerStream = 16 * 1024 * 1024;
    private static readonly UTF8Encoding _utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false
    );

    public static async Task<LocalProcessResult> RunAsync(
        LocalProcessRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        if (request.Arguments.Any(argument => argument is null))
        {
            throw new ArgumentException(
                "Process arguments cannot contain null values.",
                nameof(request)
            );
        }
        if (request.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be positive.");
        }
        if (request.MaximumOutputBytesPerStream <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum output bytes per stream must be positive."
            );
        }
        if (request.MaximumOutputBytesPerStream > MaximumAllowedOutputBytesPerStream)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Maximum output bytes per stream cannot exceed {MaximumAllowedOutputBytesPerStream}."
            );
        }
        if (request.WorkingDirectory is not null && !Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Process working directory does not exist: {request.WorkingDirectory}"
            );
        }
        if (
            request.Environment?.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null
            )
            is true
        )
        {
            throw new ArgumentException(
                "Environment variable names cannot be blank and values cannot be null.",
                nameof(request)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (request.Environment is not null)
        {
            foreach (var (name, value) in request.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        var stdout = CaptureAsync(
            process.StandardOutput.BaseStream,
            request.MaximumOutputBytesPerStream
        );
        var stderr = CaptureAsync(
            process.StandardError.BaseStream,
            request.MaximumOutputBytesPerStream
        );

        var timedOut = false;
        using var timeoutCancellation = request.Timeout is { } configuredTimeout
            ? new CancellationTokenSource(configuredTimeout)
            : null;
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation?.Token ?? CancellationToken.None
        );
        try
        {
            await process.WaitForExitAsync(waitCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            var terminationFailure = await TryTerminateAsync(process);
            if (terminationFailure is not null)
            {
                throw new TimeoutException(
                    "The process timed out and termination could not be established.",
                    terminationFailure
                );
            }
        }
        catch (OperationCanceledException)
        {
            await TryTerminateAsync(process);
            throw new OperationCanceledException(cancellationToken);
        }

        (string Text, bool Truncated)[] captured;
        try
        {
            captured = await Task.WhenAll(stdout, stderr).WaitAsync(_terminationTimeout);
        }
        catch (TimeoutException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Process output cleanup exceeded the termination grace period.",
                exception,
                cancellationToken
            );
        }
        stopwatch.Stop();
        return new LocalProcessResult(
            timedOut ? -1 : process.ExitCode,
            captured[0].Text,
            captured[1].Text,
            stopwatch.Elapsed,
            timedOut,
            captured[0].Truncated,
            captured[1].Truncated
        );
    }

    private static async Task<(string Text, bool Truncated)> CaptureAsync(
        Stream stream,
        int maximumBytes
    )
    {
        using var captured = new MemoryStream(Math.Min(maximumBytes, 8192));
        var truncated = false;
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                var copyLength = Math.Min(read, maximumBytes - checked((int)captured.Length));
                if (copyLength > 0)
                {
                    captured.Write(buffer, 0, copyLength);
                }
                truncated |= copyLength < read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return (_utf8.GetString(captured.GetBuffer(), 0, checked((int)captured.Length)), truncated);
    }

    private static async Task<Exception?> TryTerminateAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(_terminationTimeout);
            return null;
        }
        catch (Exception exception)
            when (exception
                    is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException
                        or TimeoutException
            )
        {
            try
            {
                return process.HasExited ? null : exception;
            }
            catch (InvalidOperationException)
            {
                return exception;
            }
        }
    }
}
