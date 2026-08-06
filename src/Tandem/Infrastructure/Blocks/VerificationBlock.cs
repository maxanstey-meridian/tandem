using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;
using Tandem.Infrastructure.Projection;

namespace Tandem.Infrastructure.Blocks;

public sealed class VerificationBlock(ICommandOutputObserver? outputObserver = null)
    : Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>>(BlockIds.Verify)
{
    private static readonly TimeSpan _commandTimeout = TimeSpan.FromMinutes(10);

    public override async ValueTask<PipelineMessage<SimpleV1State>> HandleAsync(
        PipelineMessage<SimpleV1State> message,
        IWorkflowContext context,
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
            return new PipelineMessage<SimpleV1State>(
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
        var result = await RunCommandAsync(command, ctx.WorkspacePath, cancellationToken);
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
        return new PipelineMessage<SimpleV1State>(
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

    private static async Task<VerificationResult> RunCommandAsync(
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

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
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

        return new VerificationResult(0, command, process.ExitCode, stdout, stderr, sw.Elapsed);
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
