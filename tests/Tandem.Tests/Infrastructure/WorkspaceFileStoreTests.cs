using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
#pragma warning disable MAAI001

namespace Tandem.Tests.Infrastructure;

public sealed class WorkspaceFileStoreTests : IDisposable
{
    private readonly string _directory = Directory
        .CreateTempSubdirectory("tandem-file-store-")
        .FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task WriteAsync_StripsLeadingUnicodeBomAndCreatesParents()
    {
        var store = new WorkspaceFileStore(_directory);

        await store.WriteAsync(
            "src/service.ts",
            "\uFEFFimport type { Todo } from './types';\n",
            default
        );

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "src", "service.ts"));
        bytes.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
        (await store.ReadAsync("src/service.ts", default)).Should().StartWith("import type");
    }

    [Fact]
    public async Task ReadAsync_ReturnsCompleteLargeContent()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "large.txt"),
            new string('x', 1_000_000)
        );
        var store = new WorkspaceFileStore(_directory);

        var content = await store.ReadAsync("large.txt", CancellationToken.None);

        content.Should().Be(new string('x', 1_000_000));
    }

    [Fact]
    public async Task ExistsReadAndDelete_ReportMissingFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "present.txt"), "value");
        var store = new WorkspaceFileStore(_directory);

        (await store.FileExistsAsync("present.txt", default)).Should().BeTrue();
        (await store.FileExistsAsync("missing.txt", default)).Should().BeFalse();
        (await store.ReadAsync("missing.txt", default)).Should().BeNull();
        (await store.DeleteAsync("present.txt", default)).Should().BeTrue();
        (await store.DeleteAsync("present.txt", default)).Should().BeFalse();
        File.Exists(Path.Combine(_directory, "present.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task MafMutationTools_OnlyReachTheSupportedStoreOperations()
    {
        using var provider = new FileAccessProvider(
            new WorkspaceFileStore(_directory),
            new FileAccessProviderOptions
            {
                DisableReadOnlyToolApproval = true,
                DisableWriteToolApproval = true,
            }
        );
        var agent = new ChatClientAgent(new UnusedChatClient());
        var context = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                agent,
                await agent.CreateSessionAsync(),
                new AIContext()
            ),
            default
        );
        var tools = context.Tools!.OfType<AIFunction>().ToDictionary(tool => tool.Name);

        await tools[FileAccessProvider.WriteToolName]
            .InvokeAsync(new() { ["fileName"] = "notes.txt", ["content"] = "one\ntwo\n" });
        await tools[FileAccessProvider.ReplaceToolName]
            .InvokeAsync(
                new()
                {
                    ["fileName"] = "notes.txt",
                    ["oldString"] = "one",
                    ["newString"] = "first",
                }
            );
        await tools[FileAccessProvider.ReplaceLinesToolName]
            .InvokeAsync(
                new()
                {
                    ["fileName"] = "notes.txt",
                    ["edits"] = JsonSerializer.SerializeToElement(
                        new[]
                        {
                            new FileLineEdit { LineNumber = 2, NewLine = "second" },
                        }
                    ),
                }
            );
        (await File.ReadAllTextAsync(Path.Combine(_directory, "notes.txt")))
            .Should()
            .StartWith("first\nsecond");
        await tools[FileAccessProvider.DeleteFileToolName]
            .InvokeAsync(new() { ["fileName"] = "notes.txt" });

        File.Exists(Path.Combine(_directory, "notes.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task EveryOperation_EnforcesTheWorkspacePathAuthority()
    {
        var outside = Directory.CreateTempSubdirectory("tandem-file-store-outside-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
            Directory.CreateSymbolicLink(Path.Combine(_directory, "linked"), outside);
            var store = new WorkspaceFileStore(_directory);

            foreach (var path in new[] { "../escape.txt", ".GIT/config", "linked/secret.txt" })
            {
                await FluentActions
                    .Awaiting(() => store.ReadAsync(path, default))
                    .Should()
                    .ThrowAsync<UnauthorizedAccessException>();
                await FluentActions
                    .Awaiting(() => store.WriteAsync(path, "unsafe", default))
                    .Should()
                    .ThrowAsync<UnauthorizedAccessException>();
                await FluentActions
                    .Awaiting(() => store.FileExistsAsync(path, default))
                    .Should()
                    .ThrowAsync<UnauthorizedAccessException>();
                await FluentActions
                    .Awaiting(() => store.DeleteAsync(path, default))
                    .Should()
                    .ThrowAsync<UnauthorizedAccessException>();
            }
            File.Exists(Path.Combine(outside, "secret.txt")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private sealed class UnusedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

#pragma warning restore MAAI001
