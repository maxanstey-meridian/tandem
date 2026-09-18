using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class AgentToolSurfaceTests
{
    [Fact]
    public void Directory_pages_return_every_entry_once()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 603; i++)
            {
                File.WriteAllText(Path.Combine(root, $"{i:D4}.txt"), "");
            }

            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var names = new List<string>();
            string? cursor = null;
            do
            {
                var page = JsonSerializer.SerializeToElement(
                    WorkspaceListTools.List(root, limit: 100, cursor: cursor)
                );
                names.AddRange(
                    page.GetProperty("entries")
                        .EnumerateArray()
                        .Select(e => e.GetProperty("name").GetString()!)
                );
                cursor = page.GetProperty("nextCursor").GetString();
            } while (cursor is not null);
            names.Should().HaveCount(603).And.OnlyHaveUniqueItems().And.BeInAscendingOrder();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Status_pages_preserve_large_inventories_and_unusual_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (await LocalProcess.RunAsync(new("git", ["init", "-q"], root))).ExitCode.Should().Be(0);
            for (var i = 0; i < 603; i++)
            {
                File.WriteAllText(Path.Combine(root, $"{i:D4}.txt"), "");
            }

            var odd = "spaces and\ttabs.txt";
            File.WriteAllText(Path.Combine(root, odd), "");
            var git = new ReadOnlyGitRepository(root);
            var paths = new List<string>();
            string? cursor = null;
            do
            {
                var page = JsonSerializer.SerializeToElement(
                    await git.StatusAsync(limit: 97, cursor: cursor)
                );
                paths.AddRange(
                    page.GetProperty("changes")
                        .EnumerateArray()
                        .Select(e => e.GetProperty("path").GetString()!)
                );
                cursor = page.GetProperty("nextCursor").GetString();
            } while (cursor is not null);
            paths.Should().HaveCount(604).And.OnlyHaveUniqueItems().And.Contain(odd);
        }
        finally
        {
            Directory.Delete(root, true);
        }
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

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var stdout = "EARLY_ERROR😀\n" + new string('a', 300000) + "\nTAIL\n";
            await File.WriteAllTextAsync(Path.Combine(root, "output.txt"), stdout);
            using var client = new CommandClient();
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
            var response = client.Result;
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
            client
                .Results.Select(r =>
                    r.GetProperty("diagnostics").GetProperty("entryCursor").GetInt64()
                )
                .Should()
                .HaveCount(2)
                .And.OnlyHaveUniqueItems();
            // A new store instance proves this reference survives reopening, not just an in-memory cache.
            var reader = (IPipelineLedgerReader)new SqliteLedgerStore(path).ForRun(result.RunId);
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
            stderr.GetProperty("content").GetString().Should().Be("stderr evidence");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class CommandClient : IChatClient
    {
        private int _calls;
        public List<JsonElement> Results { get; } = [];
        public JsonElement Result { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            if (_calls > 0)
            {
                var previous = messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>()
                    .Last()
                    .Result;
                Result = previous is JsonElement json
                    ? json
                    : JsonSerializer.SerializeToElement(previous);
                Results.Add(Result);
            }
            if (_calls < 2)
            {
                _calls++;
                return Task.FromResult(
                    new ChatResponse(
                        new ChatMessage(
                            ChatRole.Assistant,
                            [
                                new FunctionCallContent(
                                    $"cmd-{_calls}",
                                    "run_command_probe",
                                    new Dictionary<string, object?>()
                                ),
                            ]
                        )
                    )
                    {
                        FinishReason = ChatFinishReason.ToolCalls,
                    }
                );
            }
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finished."))
                {
                    FinishReason = ChatFinishReason.Stop,
                }
            );
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (
                var update in (
                    await GetResponseAsync(messages, options, cancellationToken)
                ).ToChatResponseUpdates()
            )
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
