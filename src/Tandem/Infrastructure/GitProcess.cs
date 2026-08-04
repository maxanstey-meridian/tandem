using System.Diagnostics;

namespace Tandem.Infrastructure;

public sealed record GitResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public sealed class GitProcess
{
    private const int TimeoutMs = 120_000;
    private readonly string _gitPath;

    public GitProcess(string? gitPath = null)
    {
        _gitPath = gitPath ?? "git";
    }

    public async Task<GitResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken
    )
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeoutMs);

        var startInfo = new ProcessStartInfo
        {
            FileName = _gitPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The linked CTS fires for both caller cancellation and the internal
            // 2-min timeout. Only the internal timeout counts as TimedOut.
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch { }
            await process.WaitForExitAsync(CancellationToken.None);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new GitResult(process.ExitCode, stdout, stderr, timedOut);
    }
}
