using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Infrastructure;

public sealed class WorkspaceAuthorityTests
{
    [Fact]
    public void Workspace_RejectsCommandsOwnedByAnotherWorkspace()
    {
        var first = AgentWorkspace<TestState>.Define(_ => ".", []);
        var second = AgentWorkspace<TestState>.Define(_ => ".", []);

        var configure = () =>
            Agent
                .Create<TestState>("agent", "Test.", new NoopChatClient())
                .WithWorkspace(first, [AgentTools.Always<TestState>(second.Commands)]);

        configure
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*commands from another workspace*");
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("git_status")]
    [InlineData("run_shell")]
    [InlineData("load_skill")]
    public async Task Commands_CannotImpersonateReservedTools(string name)
    {
        var workspace = new AgentWorkspaceDescriptor<TestState>(
            _ => ".",
            _ => [new AgentCommandDescriptor(name, "Reserved command.", "exit 0")],
            [
                new AgentToolGroupDescriptor<TestState>(
                    _ => true,
                    [new AgentToolSelectionDescriptor(AgentToolSelectionKind.Commands)]
                ),
            ]
        );
        var block = new AgentBlock<TestState>(
            new AgentBlockConfig<TestState>("agent", "agent", "Test.", [], _ => "Test.", workspace),
            new NoopChatClient()
        );

        var execute = async () =>
            await block.ExecuteAsync(
                new PipelineMessage<TestState>(
                    PipelineRuntime.Create(Guid.CreateVersion7()),
                    new TestState()
                ),
                CancellationToken.None
            );

        await execute
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*more than one tool named '{name}'*");
    }

    [Fact]
    public async Task ProcessExecutionGuard_BlocksUnrestrictedShellBeforeExecution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var client = new ScriptedChatClient(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                "shell-call",
                                "run_shell",
                                new Dictionary<string, object?>
                                {
                                    ["command"] = "echo blocked > blocked.txt",
                                }
                            ),
                        ]
                    )
                )
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                },
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Stopped."))
                {
                    FinishReason = ChatFinishReason.Stop,
                }
            );
            var workspace = AgentWorkspace<TestState>.Define(_ => path, []);
            var agent = Agent
                .Create<TestState>("agent", "Test.", client)
                .UseHarness("Test harness.")
                .WithWorkspace(workspace, [AgentTools.Always<TestState>("shell")])
                .WithStateGuard(
                    new AgentStateGuard<TestState>(
                        "deny-process",
                        _ => true,
                        new HashSet<Tandem.Advanced.ToolEffect>
                        {
                            Tandem.Advanced.ToolEffect.ProcessExecution,
                        },
                        "Process execution is unavailable."
                    )
                )
                .WithMessage(_ => "Try the shell.")
                .Build();
            var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
            var pipeline = Pipeline
                .Start(agent, "guard-shell")
                .Route(agent.Success, complete, "complete")
                .Build(complete);

            await new PipelineRunner().RunAsync(pipeline, new TestState());

            File.Exists(Path.Combine(path, "blocked.txt")).Should().BeFalse();
            client.CallCount.Should().Be(2);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ConditionalFileTools_AreReevaluatedAndIndividuallyFilteredPerRun()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var client = new ScriptedChatClient(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Read only."))
                {
                    FinishReason = ChatFinishReason.Stop,
                },
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Writable."))
                {
                    FinishReason = ChatFinishReason.Stop,
                }
            );
            var workspace = AgentWorkspace<TestState>.Define(_ => path, []);
            var agent = Agent
                .Create<TestState>("agent", "Inspect.", client)
                .UseHarness("Test harness.")
                .WithWorkspace(
                    workspace,
                    [
                        AgentTools.Always<TestState>("read_file"),
                        AgentTools.When<TestState>(state => state.MutationAllowed, "write_file"),
                    ]
                )
                .WithMessage(_ => "Inspect.")
                .Build();
            var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
            var pipeline = Pipeline
                .Start(agent, "conditional-tools")
                .Route(agent.Success, complete, "complete")
                .Build(complete);

            await new PipelineRunner().RunAsync(pipeline, new TestState());
            await new PipelineRunner().RunAsync(pipeline, new TestState(MutationAllowed: true));

            client.AdvertisedTools[0].Should().Contain("file_access_read");
            client.AdvertisedTools[0].Should().NotContain("file_access_write");
            client.AdvertisedTools[1].Should().Contain("file_access_read");
            client.AdvertisedTools[1].Should().Contain("file_access_write");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task FileTools_RejectGitMetadataCaseInsensitively()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-git-exclusion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, ".GIT"));
        try
        {
            var store = new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(path));

            var read = async () => await store.ReadAsync(".GIT/config", CancellationToken.None);
            var write = async () =>
                await store.WriteAsync("nested/.GiT/config", "unsafe", CancellationToken.None);
            var children = await store.ListChildrenAsync("", CancellationToken.None);

            await read.Should().ThrowAsync<UnauthorizedAccessException>();
            await write.Should().ThrowAsync<UnauthorizedAccessException>();
            children.Should().NotContain(entry => entry.Name == ".GIT");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record TestState(bool MutationAllowed = false);

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        internal int CallCount { get; private set; }
        internal List<IReadOnlyList<string>> AdvertisedTools { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            AdvertisedTools.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
            return Task.FromResult(_responses.Dequeue());
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

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("The model must not be called.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
