using System.Diagnostics;
using System.Text.Json;
using Tandem.Domain;
using Tandem.Infrastructure.Projection;

namespace Tandem.Infrastructure.Blocks;

public sealed class VerificationBlock(
    ICommandOutputObserver? outputObserver = null,
    TimeSpan? commandTimeout = null
)
{
    private readonly TimeSpan _commandTimeout = commandTimeout ?? TimeSpan.FromMinutes(10);

    public async ValueTask<PipelineMessage<DeliveryState>> ExecuteAsync(
        PipelineMessage<DeliveryState> message,
        CancellationToken cancellationToken
    )
    {
        var blockSw = Stopwatch.StartNew();
        var ctx = message.State;
        var commands = ctx.Packet.Verification;

        if (ctx.VerificationIndex >= commands.Count)
        {
            var allPassed = ctx.VerificationResults.All(r => r.ExitCode == 0);
            var finalKind = allPassed ? OutcomeKinds.CommandPassed : OutcomeKinds.CommandFailed;
            blockSw.Stop();
            return new PipelineMessage<DeliveryState>(
                message.Runtime,
                ctx,
                new BlockOutcome(
                    finalKind,
                    BlockIds.Verify,
                    "All verification commands complete",
                    JsonSerializer.SerializeToElement(new { }),
                    blockSw.Elapsed
                )
            );
        }

        var command = commands[ctx.VerificationIndex];
        var result = await RunCommandAsync(
            ctx.VerificationIndex,
            command,
            ctx.WorkspacePath,
            cancellationToken
        );
        if (result.ExitCode == 0)
        {
            result = await RejectCandidateMutationAsync(result, ctx, cancellationToken);
        }
        if (outputObserver is not null)
        {
            var output = string.Join(
                Environment.NewLine,
                new[] { result.Stdout, result.Stderr }.Where(value => !string.IsNullOrEmpty(value))
            );
            await outputObserver.CommandOutputAsync(
                BlockIds.Verify,
                command,
                output,
                result.ExitCode,
                cancellationToken
            );
        }

        var results = ctx.VerificationResults.Append(result).ToList();
        var passed = result.ExitCode == 0;
        var newIndex = passed ? ctx.VerificationIndex + 1 : ctx.VerificationIndex;

        var updatedContext = ctx with
        {
            VerificationIndex = newIndex,
            VerificationResults = results,
        };

        var kind = passed ? OutcomeKinds.CommandPassed : OutcomeKinds.CommandFailed;
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                index = result.Index,
                exitCode = result.ExitCode,
                elapsedMs = result.Elapsed.TotalMilliseconds,
            }
        );

        blockSw.Stop();
        return new PipelineMessage<DeliveryState>(
            message.Runtime,
            updatedContext,
            new BlockOutcome(
                kind,
                BlockIds.Verify,
                passed ? "Command passed" : "Command failed",
                payload,
                blockSw.Elapsed
            )
        );
    }

    private async Task<VerificationResult> RunCommandAsync(
        int index,
        string command,
        string workspacePath,
        CancellationToken cancellationToken
    )
    {
        var (fileName, args) = BuildProcessStart(command);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_commandTimeout);

        var sw = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workspacePath,
            },
            EnableRaisingEvents = true,
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

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
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch { }
            await process.WaitForExitAsync(CancellationToken.None);
        }

        sw.Stop();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        cancellationToken.ThrowIfCancellationRequested();

        if (timedOut)
        {
            stderr = string.Join(
                Environment.NewLine,
                new[]
                {
                    stderr,
                    $"Command timed out after {_commandTimeout.TotalSeconds:0.###} seconds.",
                }.Where(value => !string.IsNullOrWhiteSpace(value))
            );
        }

        return new VerificationResult(
            index,
            command,
            timedOut ? -1 : process.ExitCode,
            stdout,
            stderr,
            sw.Elapsed,
            timedOut
        );
    }

    private static async Task<VerificationResult> RejectCandidateMutationAsync(
        VerificationResult result,
        DeliveryState state,
        CancellationToken cancellationToken
    )
    {
        var git = new GitProcess();
        var head = await git.RunAsync(
            state.WorkspacePath,
            ["rev-parse", "HEAD"],
            cancellationToken
        );
        var status = await git.RunAsync(
            state.WorkspacePath,
            ["status", "--porcelain"],
            cancellationToken
        );
        var candidateUnchanged =
            head.ExitCode == 0
            && status.ExitCode == 0
            && string.Equals(
                head.Stdout.Trim(),
                state.CandidateSha,
                StringComparison.OrdinalIgnoreCase
            )
            && string.IsNullOrWhiteSpace(status.Stdout);
        if (candidateUnchanged)
        {
            return result;
        }

        var evidence = string.Join(
            Environment.NewLine,
            new[]
            {
                result.Stderr,
                "Verification modified the captured candidate. Verification commands must be read-only.",
                head.ExitCode == 0
                    ? $"HEAD: {head.Stdout.Trim()}"
                    : $"git rev-parse failed: {head.Stderr}",
                status.ExitCode == 0 ? status.Stdout : $"git status failed: {status.Stderr}",
            }.Where(value => !string.IsNullOrWhiteSpace(value))
        );
        return result with { ExitCode = -1, Stderr = evidence };
    }

    private static (string FileName, string[] Args) BuildProcessStart(string command)
    {
        if (OperatingSystem.IsMacOS())
        {
            return ("/bin/zsh", ["-lc", command]);
        }

        if (OperatingSystem.IsLinux())
        {
            return ("/bin/bash", ["-lc", command]);
        }

        return ("cmd.exe", ["/d", "/s", "/c", command]);
    }
}
