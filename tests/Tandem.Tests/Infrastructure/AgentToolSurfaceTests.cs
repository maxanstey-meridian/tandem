using System.Text;
using System.Text.Json;
using FluentAssertions;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class AgentToolSurfaceTests
{
    [Fact]
    public void Directory_pages_return_every_entry_once()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        for (var i = 0; i < 603; i++)
        {
            File.WriteAllText(Path.Combine(root, $"{i:D4}.txt"), "");
        }

        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var names = new List<string>();
        var offset = 0;
        do
        {
            var page = JsonSerializer.SerializeToElement(
                WorkspaceListTools.List(root, limit: 100, offset: offset)
            );
            names.AddRange(
                page.GetProperty("entries")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("name").GetString()!)
            );
            offset = page.TryGetProperty("nextOffset", out var next) ? next.GetInt32() : -1;
        } while (offset >= 0);
        names.Should().HaveCount(603).And.OnlyHaveUniqueItems().And.BeInAscendingOrder();
    }

    [Fact]
    public async Task Status_pages_preserve_large_inventories_and_unusual_paths()
    {
        using var temp = new TempDirectory();
        var root = temp.Path;
        (await LocalProcess.RunAsync(new("git", ["init", "-q"], root))).ExitCode.Should().Be(0);
        for (var i = 0; i < 603; i++)
        {
            File.WriteAllText(Path.Combine(root, $"{i:D4}.txt"), "");
        }

        var odd = "spaces and\ttabs.txt";
        File.WriteAllText(Path.Combine(root, odd), "");
        var git = new ReadOnlyGitRepository(root);
        var paths = new List<string>();
        var offset = 0;
        JsonElement last = default;
        do
        {
            var page = JsonSerializer.SerializeToElement(
                await git.StatusAsync(limit: 97, offset: offset)
            );
            last = page;
            paths.AddRange(
                page.GetProperty("changes")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("path").GetString()!)
            );
            var more =
                page.TryGetProperty("nextOffset", out var nextOffset)
                && nextOffset.ValueKind != JsonValueKind.Null;
            offset = more ? nextOffset.GetInt32() : -1;
        } while (offset >= 0);
        paths.Should().HaveCount(604).And.OnlyHaveUniqueItems().And.Contain(odd);
        // The final page carries no continuation position: the payload holds
        // branch and changes without derivable counts or instruction strings.
        last.GetProperty("nextOffset").ValueKind.Should().Be(JsonValueKind.Null);
        last.TryGetProperty("hasMore", out _).Should().BeFalse();
        last.TryGetProperty("returnedCount", out _).Should().BeFalse();
        last.TryGetProperty("nextCursor", out _).Should().BeFalse();
        last.TryGetProperty("pagination", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Large_command_diagnostics_are_persisted_before_bounded_presentation(
        bool enableLedger
    )
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var root = temp.Path;
        var stdout = "EARLY_ERROR😀\n" + new string('a', 300000) + "\nTAIL\n";
        await File.WriteAllTextAsync(Path.Combine(root, "output.txt"), stdout);
        using var client = new TestChatClient(
            TestChatClient.ToolCall("run_command_probe", callId: "cmd-1"),
            TestChatClient.ToolCall("run_command_probe", callId: "cmd-2"),
            TestChatClient.Text("Finished.")
        );
        var workspace = AgentWorkspace<string>.Define(
            _ => root,
            [
                AgentCommand.Define(
                    "run_command_probe",
                    "Emit diagnostics.",
                    "cat output.txt; printf 'stderr evidence' >&2; exit 7"
                ),
            ]
        );
        var agent = Agent
            .Create<string>("probe", "Run the command.", client)
            .UseHarness("Use the tools.")
            .WithWorkspace(workspace, [AgentTools.Always<string>(workspace.Commands)])
            .WithMessage(s => s)
            .Build();
        var path = Path.Combine(root, "ledger.sqlite3");
        var pipeline = Pipeline.Start(agent, "diagnostic-test").Persist().Build(agent);
        var result = await new PipelineRunner().RunAsync(
            pipeline,
            "Run.",
            new SqlitePipelineRunOptions(path) { EnableLedgerTools = enableLedger }
        );
        result.Succeeded.Should().BeTrue();
        var results = client.ToolResults();
        var response = results["cmd-2"];
        response.GetRawText().Length.Should().BeLessThan(17000);
        response.GetProperty("exitCode").GetInt32().Should().Be(7);
        response.GetProperty("previewTruncated").GetBoolean().Should().BeTrue();
        response.GetProperty("captureTruncated").GetBoolean().Should().BeFalse();
        if (!enableLedger)
        {
            response.GetProperty("diagnostics").ValueKind.Should().Be(JsonValueKind.Null);
            return;
        }
        var cursor = response.GetProperty("diagnostics").GetProperty("entryCursor").GetInt64();
        new[] { results["cmd-1"], response }
            .Select(r => r.GetProperty("diagnostics").GetProperty("entryCursor").GetInt64())
            .Should()
            .HaveCount(2)
            .And.OnlyHaveUniqueItems();
        // A new store instance proves this reference survives reopening, not just an in-memory cache.
        var reader = new SqliteLedgerStore(path).ForRun(result.RunId);
        var text = new StringBuilder();
        var offset = 0;
        do
        {
            var page = JsonSerializer.SerializeToElement(
                await reader.ReadDiagnosticAsync(cursor, "stdout", offset, 12000)
            );
            text.Append(page.GetProperty("content").GetString());
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }

            offset = page.GetProperty("nextOffset").GetInt32();
        } while (true);
        text.ToString().Should().Be(stdout);
        var stderr = JsonSerializer.SerializeToElement(
            await reader.ReadDiagnosticAsync(cursor, "stderr")
        );
        stderr.GetProperty("content").GetString().Should().Be("stderr evidence\n");
    }
}
