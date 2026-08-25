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
    public void Tavily_tools_are_accepted_and_resolved_through_existing_tool_groups()
    {
        var always = AgentTools.Always<TestState>("web_search");
        var conditional = AgentTools.When<TestState>(state => state.MutationAllowed, "web_fetch");

        always.Descriptor.IsAvailable(new TestState()).Should().BeTrue();
        always.Descriptor.Tools.Should().ContainSingle(tool => tool.Name == "web_search");
        conditional.Descriptor.IsAvailable(new TestState()).Should().BeFalse();
        conditional.Descriptor.IsAvailable(new TestState(MutationAllowed: true)).Should().BeTrue();
        conditional.Descriptor.Tools.Should().ContainSingle(tool => tool.Name == "web_fetch");
    }

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
    [InlineData("web_search")]
    [InlineData("web_fetch")]
    [InlineData("git_status")]
    [InlineData("run_shell")]
    [InlineData("load_skill")]
    public async Task Commands_CannotImpersonateReservedTools(string name)
    {
        var workspace = new AgentWorkspaceDescriptor<TestState>(
            _ => ".",
            _ => [new AgentCommandDescriptor(name, "Reserved command.", "exit 0", [])],
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
                        AgentTools.When<TestState>(
                            state => state.MutationAllowed,
                            "write_file",
                            "copy_file",
                            "move_file",
                            "create_directory"
                        ),
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

            client.AdvertisedTools[0].Count(name => name == "file_access_read").Should().Be(1);
            client.AdvertisedTools[0].Should().NotContain("file_access_write");
            client.AdvertisedTools[0].Should().NotContain(WorkspaceFileMutationTools.CopyToolName);
            client.AdvertisedTools[0].Should().NotContain(WorkspaceFileMutationTools.MoveToolName);
            client
                .AdvertisedTools[0]
                .Should()
                .NotContain(WorkspaceFileMutationTools.CreateDirectoryToolName);
            client.AdvertisedTools[1].Count(name => name == "file_access_read").Should().Be(1);
            client.AdvertisedTools[1].Should().Contain("file_access_write");
            client.AdvertisedTools[1].Should().Contain(WorkspaceFileMutationTools.CopyToolName);
            client.AdvertisedTools[1].Should().Contain(WorkspaceFileMutationTools.MoveToolName);
            client
                .AdvertisedTools[1]
                .Should()
                .Contain(WorkspaceFileMutationTools.CreateDirectoryToolName);
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

    [Fact]
    public void CopyAndMoveFile_PreserveBytesAndEnforceWorkspaceBoundaries()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"tandem-file-mutation-{Guid.NewGuid():N}");
        var workspace = Path.Combine(parent, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "source"));
        Directory.CreateDirectory(Path.Combine(workspace, "destination"));
        try
        {
            WorkspaceFileMutationTools.CreateDirectory(
                workspace,
                "generated/nested",
                CancellationToken.None
            );
            Directory.Exists(Path.Combine(workspace, "generated", "nested")).Should().BeTrue();
            WorkspaceFileMutationTools.CreateDirectory(
                workspace,
                "generated/nested",
                CancellationToken.None
            );

            var bytes = new byte[] { 0, 1, 2, 127, 128, 255 };
            File.WriteAllBytes(Path.Combine(workspace, "source", "payload.bin"), bytes);

            WorkspaceFileMutationTools.Copy(
                workspace,
                "source/payload.bin",
                "destination/copied.bin",
                overwrite: false,
                CancellationToken.None
            );
            File.ReadAllBytes(Path.Combine(workspace, "destination", "copied.bin"))
                .Should()
                .Equal(bytes);

            var replacement = new byte[] { 9, 8, 7 };
            File.WriteAllBytes(Path.Combine(workspace, "source", "payload.bin"), replacement);
            var refuseOverwrite = () =>
                WorkspaceFileMutationTools.Copy(
                    workspace,
                    "source/payload.bin",
                    "destination/copied.bin",
                    overwrite: false,
                    CancellationToken.None
                );
            refuseOverwrite.Should().Throw<IOException>();
            WorkspaceFileMutationTools.Copy(
                workspace,
                "source/payload.bin",
                "destination/copied.bin",
                overwrite: true,
                CancellationToken.None
            );

            WorkspaceFileMutationTools.Move(
                workspace,
                "destination/copied.bin",
                "destination/moved.bin",
                overwrite: false,
                CancellationToken.None
            );
            File.Exists(Path.Combine(workspace, "destination", "copied.bin")).Should().BeFalse();
            File.ReadAllBytes(Path.Combine(workspace, "destination", "moved.bin"))
                .Should()
                .Equal(replacement);

            var escape = () =>
                WorkspaceFileMutationTools.Copy(
                    workspace,
                    "source/payload.bin",
                    "../escaped.bin",
                    overwrite: false,
                    CancellationToken.None
                );
            escape.Should().Throw<UnauthorizedAccessException>();

            var git = () =>
                WorkspaceFileMutationTools.Move(
                    workspace,
                    "source/payload.bin",
                    ".GIT/payload.bin",
                    overwrite: false,
                    CancellationToken.None
                );
            git.Should().Throw<UnauthorizedAccessException>();

            var createGit = () =>
                WorkspaceFileMutationTools.CreateDirectory(
                    workspace,
                    ".git/generated",
                    CancellationToken.None
                );
            createGit.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void FileMutations_RejectLinksThatEscapeTheWorkspace()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"tandem-file-link-{Guid.NewGuid():N}");
        var workspace = Path.Combine(parent, "workspace");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        try
        {
            var secret = Path.Combine(outside, "secret.txt");
            File.WriteAllText(secret, "secret");
            File.CreateSymbolicLink(Path.Combine(workspace, "secret-link.txt"), secret);
            Directory.CreateSymbolicLink(Path.Combine(workspace, "outside-link"), outside);
            File.WriteAllText(Path.Combine(workspace, "source.txt"), "source");

            var copyFromLink = () =>
                WorkspaceFileMutationTools.Copy(
                    workspace,
                    "secret-link.txt",
                    "copied-secret.txt",
                    overwrite: false,
                    CancellationToken.None
                );
            var copyThroughLink = () =>
                WorkspaceFileMutationTools.Copy(
                    workspace,
                    "source.txt",
                    "outside-link/copied.txt",
                    overwrite: false,
                    CancellationToken.None
                );
            var createThroughLink = () =>
                WorkspaceFileMutationTools.CreateDirectory(
                    workspace,
                    "outside-link/generated",
                    CancellationToken.None
                );

            copyFromLink.Should().Throw<UnauthorizedAccessException>();
            copyThroughLink.Should().Throw<UnauthorizedAccessException>();
            createThroughLink.Should().Throw<UnauthorizedAccessException>();
            File.Exists(Path.Combine(workspace, "copied-secret.txt")).Should().BeFalse();
            File.Exists(Path.Combine(outside, "copied.txt")).Should().BeFalse();
            Directory.Exists(Path.Combine(outside, "generated")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
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
