using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class PaginationRecoveryTests
{
    [Fact]
    public async Task Invalid_line_requests_recover_and_grep_locations_can_be_read()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        await File.WriteAllTextAsync(
            Path.Combine(directory, "text.txt"),
            "before\r\n😀 target\rafter\n"
        );
        using var client = PagingClient([
            Call("negative", "file_access_read", new { path = "text.txt", startLine = -1 }),
            Call("zero", "file_access_read", new { path = "text.txt", lineCount = 0 }),
            Call("large", "file_access_read", new { path = "text.txt", lineCount = 2001 }),
            Call("past", "file_access_read", new { path = "text.txt", startLine = 1600 }),
            Call("grep", "file_access_grep", new { regexPattern = "target" }),
            Call(
                "corrected",
                "file_access_read",
                new
                {
                    path = "text.txt",
                    startLine = 2,
                    lineCount = 2,
                }
            ),
        ]);
        await Run(directory, client);
        foreach (var id in new[] { "negative", "zero", "large", "past" })
        {
            client.ToolResults()[id].GetProperty("isError").GetBoolean().Should().BeTrue();
        }

        client
            .ToolResults()["grep"]
            .GetProperty("matches")[0]
            .GetProperty("line")
            .GetInt32()
            .Should()
            .Be(2);
        client
            .ToolResults()["corrected"]
            .GetProperty("lines")
            .EnumerateArray()
            .Select(line => line.GetString())
            .Should()
            .Equal("😀 target", "after");
        client.ToolResults()["corrected"].TryGetProperty("totalLines", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Out_of_range_grep_offset_is_recovered_and_record_search_succeeds()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        await File.WriteAllTextAsync(Path.Combine(directory, "text.txt"), "x\nx");
        using var client = PagingClient([
            Call("past", "file_access_grep", new { regexPattern = "x", offset = 10 }),
            Call("corrected", "file_access_grep", new { regexPattern = "x", limit = 1 }),
        ]);
        await Run(directory, client);
        // An offset beyond the match count is ordinary range validation;
        // there is no snapshot check to fail, and the corrected call proceeds.
        client.ToolResults()["corrected"].GetProperty("nextOffset").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Ledger_argument_errors_are_recoverable_and_pages_explain_continuation()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        using var client = PagingClient([
            Call("cursor", "read_ledger", new { cursor = -1 }),
            Call("zero", "read_ledger", new { limit = 0 }),
            Call("large", "read_ledger", new { limit = 51 }),
            Call("blank", "search_ledger", new { query = " " }),
            Call("long", "search_ledger", new { query = new string('x', 1025) }),
            Call("corrected", "read_ledger", new { cursor = long.MaxValue, limit = 1 }),
        ]);
        await Run(directory, client, ledger: true);
        foreach (var id in new[] { "cursor", "zero", "large", "blank", "long" })
        {
            client.ToolResults()[id].GetProperty("isError").GetBoolean().Should().BeTrue();
        }
        var page = client.ToolResults()["corrected"];
        page.GetProperty("returnedCount").GetInt32().Should().Be(0);
        page.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Obsolete_arguments_and_bad_searches_are_feedback_then_corrected_read_succeeds()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        await File.WriteAllTextAsync(Path.Combine(directory, "text.txt"), "first\nsecond\nthird");
        using var client = PagingClient([
            Call(
                "old",
                "file_access_read",
                new
                {
                    path = "text.txt",
                    offset = 7,
                    limit = 3,
                }
            ),
            Call("typo", "file_access_read", new { path = "text.txt", startline = 2 }),
            Call("type", "file_access_read", new { path = "text.txt", startLine = "not-a-line" }),
            Call(
                "overflow",
                "file_access_read",
                new { path = "text.txt", startLine = long.MaxValue }
            ),
            Call("regex", "file_access_grep", new { regexPattern = "[" }),
            Call(
                "directory",
                "file_access_grep",
                new { regexPattern = "x", directory = "missing" }
            ),
            Call("missing", "file_access_read", new { path = "missing.txt" }),
            Call(
                "corrected",
                "file_access_read",
                new
                {
                    path = "text.txt",
                    startLine = 2,
                    lineCount = 1,
                }
            ),
        ]);
        await Run(directory, client);
        foreach (
            var id in new[] { "old", "typo", "type", "overflow", "regex", "directory", "missing" }
        )
        {
            client.ToolResults()[id].GetProperty("isError").GetBoolean().Should().BeTrue();
        }
        client
            .ToolResults()["old"]
            .GetProperty("message")
            .GetString()
            .Should()
            .Contain("startLine");
        client.ToolResults()["old"].TryGetProperty("content", out _).Should().BeFalse();
        client.ToolResults()["corrected"].GetProperty("lines")[0].GetString().Should().Be("second");
    }

    [Fact]
    public async Task Ordinary_mutation_and_path_errors_allow_corrected_calls()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        await File.WriteAllTextAsync(Path.Combine(directory, "source.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(directory, "exists.txt"), "untouched");
        using var client = PagingClient([
            Call(
                "missing",
                "file_access_copy",
                new
                {
                    sourceFileName = "missing.txt",
                    destinationFileName = "new.txt",
                    overwrite = false,
                }
            ),
            Call(
                "exists",
                "file_access_move",
                new
                {
                    sourceFileName = "source.txt",
                    destinationFileName = "exists.txt",
                    overwrite = false,
                }
            ),
            Call("escape", "file_access_read", new { path = "../outside.txt" }),
            Call(
                "corrected",
                "file_access_copy",
                new
                {
                    sourceFileName = "source.txt",
                    destinationFileName = "new.txt",
                    overwrite = false,
                }
            ),
        ]);
        await Run(directory, client);
        foreach (var id in new[] { "missing", "exists", "escape" })
        {
            client.ToolResults()[id].GetProperty("isError").GetBoolean().Should().BeTrue();
        }

        File.ReadAllText(Path.Combine(directory, "exists.txt")).Should().Be("untouched");
        File.ReadAllText(Path.Combine(directory, "new.txt")).Should().Be("keep");
    }

    private static async Task Run(string directory, TestChatClient client, bool ledger = false)
    {
        var workspace = AgentWorkspace<string>.Define(_ => directory, []);
        var agent = Agent
            .Create<string>("reader", "Read the requested pages.", client)
            .UseHarness("Use tools; correct invalid arguments.")
            .WithWorkspace(
                workspace,
                [AgentTools.Always<string>("read_file", "grep", "copy_file", "move_file", "git:ro")]
            )
            .WithMessage(state => state)
            .Build();
        if (ledger)
        {
            var pipeline = Pipeline.Start(agent, "paging").Persist().Build(agent);
            var result = await new PipelineRunner().RunAsync(
                pipeline,
                "Read.",
                new SqlitePipelineRunOptions(Path.Combine(directory, "ledger.sqlite3"))
                {
                    EnableLedgerTools = true,
                }
            );
            result.Succeeded.Should().BeTrue();
        }
        else
        {
            var result = await new PipelineRunner().RunAsync(
                Pipeline.Start(agent, "paging").Build(agent),
                "Read."
            );
            result.Succeeded.Should().BeTrue();
        }
    }

    private static ChatResponse Call(string id, string name, object args) =>
        new(
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        id,
                        name,
                        JsonSerializer.Deserialize<Dictionary<string, object?>>(
                            JsonSerializer.Serialize(args)
                        )!
                    ),
                ]
            )
        )
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private static TestChatClient PagingClient(ChatResponse[] calls) =>
        new([.. calls, TestChatClient.Text("Done.")]);
}
