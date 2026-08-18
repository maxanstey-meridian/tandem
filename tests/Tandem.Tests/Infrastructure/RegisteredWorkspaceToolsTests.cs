using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class RegisteredWorkspaceToolsTests
{
    [Fact]
    public async Task Registered_tool_is_exposed_with_workspace_path_and_repository_evidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-registered-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            string? invokedPath = null;
            var workspace = AgentWorkspace<TestState>.Define(_ => path, []);
            var inspect = workspace.Register(
                AgentWorkspaceTool.Define(
                    "inspect_repository",
                    workspacePath =>
                        AIFunctionFactory.Create(
                            (CancellationToken _) =>
                            {
                                invokedPath = workspacePath;
                                return "inspected";
                            },
                            "inspect_repository",
                            "Inspect the repository."
                        ),
                    ToolEffect.ProcessExecution,
                    ToolEvidence.RepositoryInspection
                )
            );
            var client = new ScriptedChatClient(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                "inspect-call",
                                "inspect_repository",
                                new Dictionary<string, object?>()
                            ),
                        ]
                    )
                )
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                },
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
                {
                    FinishReason = ChatFinishReason.Stop,
                }
            );
            var agent = Agent
                .Create<TestState>("agent", "Inspect.", client)
                .UseHarness("Test harness.")
                .WithWorkspace(workspace, [AgentTools.Always<TestState>(inspect)])
                .WithMessage(_ => "Inspect.")
                .Build();
            var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
            var pipeline = Pipeline
                .Start(agent, "registered-tool")
                .Route(agent.Success, complete, "complete")
                .Build(complete);

            var result = await new PipelineRunner().RunAsync(pipeline, new TestState());

            result.Status.Should().Be(PipelineRunStatus.Succeeded);
            invokedPath.Should().Be(Path.GetFullPath(path));
            client
                .AdvertisedTools.Should()
                .NotBeEmpty()
                .And.OnlyContain(tools => tools.Contains("inspect_repository"));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void Workspace_rejects_duplicate_registered_names()
    {
        var workspace = AgentWorkspace<TestState>.Define(_ => ".", []);
        var tool = AgentWorkspaceTool.Define(
            "inspect_repository",
            _ =>
                AIFunctionFactory.Create(
                    () => "inspected",
                    "inspect_repository",
                    "Inspect the repository."
                ),
            ToolEffect.Read
        );
        workspace.Register(tool);

        var register = () => workspace.Register(tool);

        register.Should().Throw<ArgumentException>().WithMessage("*already registered*");
    }

    private sealed record TestState;

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        internal List<IReadOnlyList<string>> AdvertisedTools { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
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
}
