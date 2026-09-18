using System.Runtime.CompilerServices;
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
        var directory = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "text.txt"),
                "before\r\n😀 target\rafter\n"
            );
            using var client = new PagingClient([
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
                client.Results[id].GetProperty("isError").GetBoolean().Should().BeTrue();
            }

            client
                .Results["grep"]
                .GetProperty("matches")[0]
                .GetProperty("line")
                .GetInt32()
                .Should()
                .Be(2);
            client
                .Results["corrected"]
                .GetProperty("lines")
                .EnumerateArray()
                .Select(line => line.GetProperty("text").GetString())
                .Should()
                .Equal("😀 target", "after");
            client.Results["corrected"].GetProperty("totalLines").GetInt32().Should().Be(3);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Obsolete_grep_offset_is_rejected_and_record_search_succeeds()
    {
        var directory = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "text.txt"), "x\nx");
            using var client = new PagingClient([
                Call("old", "file_access_grep", new { regexPattern = "x", offset = 10 }),
                Call("corrected", "file_access_grep", new { regexPattern = "x", limit = 1 }),
            ]);
            await Run(directory, client);
            client.Results["old"].GetProperty("isError").GetBoolean().Should().BeTrue();
            client.Results["corrected"].GetProperty("matches").GetArrayLength().Should().Be(1);
            client
                .Results["corrected"]
                .GetProperty("nextCursor")
                .GetString()
                .Should()
                .NotBeNullOrEmpty();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Ledger_argument_errors_are_recoverable_and_pages_explain_continuation()
    {
        var directory = CreateDirectory();
        try
        {
            using var client = new PagingClient([
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
                client.Results[id].GetProperty("isError").GetBoolean().Should().BeTrue();
            }
            var page = client.Results["corrected"];
            page.GetProperty("returnedCount").GetInt32().Should().Be(0);
            page.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Obsolete_arguments_and_bad_searches_are_feedback_then_corrected_read_succeeds()
    {
        var directory = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "text.txt"),
                "first\nsecond\nthird"
            );
            using var client = new PagingClient([
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
                Call(
                    "type",
                    "file_access_read",
                    new { path = "text.txt", startLine = "not-a-line" }
                ),
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
                var id in new[]
                {
                    "old",
                    "typo",
                    "type",
                    "overflow",
                    "regex",
                    "directory",
                    "missing",
                }
            )
            {
                client.Results[id].GetProperty("isError").GetBoolean().Should().BeTrue();
            }
            client.Results["old"].GetProperty("message").GetString().Should().Contain("startLine");
            client.Results["old"].TryGetProperty("content", out _).Should().BeFalse();
            client
                .Results["corrected"]
                .GetProperty("lines")[0]
                .GetProperty("text")
                .GetString()
                .Should()
                .Be("second");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Ordinary_mutation_and_path_errors_allow_corrected_calls()
    {
        var directory = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "source.txt"), "keep");
            await File.WriteAllTextAsync(Path.Combine(directory, "exists.txt"), "untouched");
            using var client = new PagingClient([
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
                client.Results[id].GetProperty("isError").GetBoolean().Should().BeTrue();
            }

            File.ReadAllText(Path.Combine(directory, "exists.txt")).Should().Be("untouched");
            File.ReadAllText(Path.Combine(directory, "new.txt")).Should().Be("keep");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "pagination-recovery-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task Run(string directory, PagingClient client, bool ledger = false)
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

    private sealed class PagingClient(ChatResponse[] calls, Action<int>? beforeCall = null)
        : IChatClient
    {
        private int _index;
        public Dictionary<string, JsonElement> Results { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            foreach (
                var result in messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            )
            {
                Results[result.CallId] = result.Result is JsonElement json
                    ? json
                    : JsonSerializer.SerializeToElement(result.Result);
            }
            beforeCall?.Invoke(_index);
            return Task.FromResult(
                _index < calls.Length
                    ? calls[_index++]
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
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
