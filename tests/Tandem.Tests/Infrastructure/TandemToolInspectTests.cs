using System.Diagnostics;
using FluentAssertions;
using Tandem.Infrastructure;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class TandemToolInspectTests
{
    [Fact]
    public async Task Inspect_LedgerOptionReadsTheSelectedDatabase()
    {
        var ledgerPath = Path.Combine(
            Path.GetTempPath(),
            $"tandem-tool-inspect-{Guid.NewGuid():N}.sqlite3"
        );
        var runId = Guid.CreateVersion7();
        try
        {
            var store = new SqliteLedgerStore(ledgerPath);
            await store.InitializeAsync();
            await store.CreateRunAsync(runId, "fixture");
            await store.CompleteRunAsync(runId, LedgerRunStatus.Ready);

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add(typeof(ChatClientBuilder).Assembly.Location);
            start.ArgumentList.Add("inspect");
            start.ArgumentList.Add(runId.ToString("N"));
            start.ArgumentList.Add("--ledger");
            start.ArgumentList.Add(ledgerPath);

            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);

            process.ExitCode.Should().Be(0, error);
            output.Should().Contain($"Run {runId:N}  fixture  Ready");
        }
        finally
        {
            File.Delete(ledgerPath);
            File.Delete($"{ledgerPath}-shm");
            File.Delete($"{ledgerPath}-wal");
        }
    }
}
