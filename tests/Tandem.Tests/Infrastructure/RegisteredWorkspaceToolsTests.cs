using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class RegisteredWorkspaceToolsTests
{
    [Fact]
    public async Task Registered_tool_is_exposed_with_workspace_path_and_repository_evidence()
    {
        using var temp = new TempDirectory();
        var path = temp.Path;
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
        var client = new TestChatClient(
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

    [Theory]
    [InlineData("read_file")]
    [InlineData("copy_file")]
    [InlineData("move_file")]
    [InlineData("create_directory")]
    public async Task Registered_tool_cannot_take_a_workspace_tool_name(string name)
    {
        var workspace = AgentWorkspace<TestState>.Define(_ => Path.GetTempPath(), []);
        var colliding = workspace.Register(
            AgentWorkspaceTool.Define(
                name,
                _ => AIFunctionFactory.Create(() => "collided", name, "Collide."),
                ToolEffect.Read
            )
        );
        var client = new TestChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))
            {
                FinishReason = ChatFinishReason.Stop,
            }
        );
        var agent = Agent
            .Create<TestState>("agent", "Inspect.", client)
            .UseHarness("Test harness.")
            .WithWorkspace(workspace, [AgentTools.Always<TestState>(colliding)])
            .WithMessage(_ => "Inspect.")
            .Build();
        var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
        var pipeline = Pipeline
            .Start(agent, "colliding-tool")
            .Route(agent.Success, complete, "complete")
            .Build(complete);

        var run = () => new PipelineRunner().RunAsync(pipeline, new TestState());

        (await run.Should().ThrowAsync<PipelineRunException>())
            .Which.ToString()
            .Should()
            .Contain($"exposes more than one tool named '{name}'");
        client.AdvertisedTools.Should().BeEmpty();
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
}
