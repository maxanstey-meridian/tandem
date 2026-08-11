using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class AgentSkillTests
{
    [Fact]
    public void FromDirectory_RequiresAnExistingSkillEntryPoint()
    {
        using var directory = new TemporaryDirectory();

        var missingEntryPoint = () => AgentSkill.FromDirectory(directory.Path);
        missingEntryPoint.Should().Throw<FileNotFoundException>().WithMessage("*SKILL.md*");

        var missingDirectory = () =>
            AgentSkill.FromDirectory(System.IO.Path.Combine(directory.Path, "missing"));
        missingDirectory.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Builder_RejectsDuplicateNormalizedSkillDirectories()
    {
        using var directory = TemporaryDirectory.WithSkill();
        var skill = AgentSkill.FromDirectory(directory.Path);

        var duplicate = () =>
            Agent
                .Create<TestState>("agent", "Respond.", new RecordingChatClient())
                .WithSkill(skill)
                .WithSkill(AgentSkill.FromDirectory(System.IO.Path.Combine(directory.Path, ".")));

        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*more than once*");
    }

    [Fact]
    public async Task AttachedDirectory_UsesMafLoadAndResourceToolsWithoutScripts()
    {
        using var root = new TemporaryDirectory();
        var skillDirectory = System.IO.Path.Combine(root.Path, "test-skill");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            System.IO.Path.Combine(skillDirectory, "SKILL.md"),
            """
            ---
            name: test-skill
            description: Test discovery.
            ---

            # Test Skill

            Follow the test doctrine.
            """
        );
        Directory.CreateDirectory(System.IO.Path.Combine(skillDirectory, "references"));
        File.WriteAllText(
            System.IO.Path.Combine(skillDirectory, "references", "rules.md"),
            "Prefer explicit boundaries."
        );
        var nestedSkillDirectory = System.IO.Path.Combine(
            skillDirectory,
            "references",
            "nested-skill"
        );
        Directory.CreateDirectory(nestedSkillDirectory);
        File.WriteAllText(
            System.IO.Path.Combine(nestedSkillDirectory, "SKILL.md"),
            "---\nname: nested-skill\ndescription: Must remain unavailable.\n---\n\nIgnore."
        );
        Directory.CreateDirectory(System.IO.Path.Combine(skillDirectory, "scripts"));
        File.WriteAllText(
            System.IO.Path.Combine(skillDirectory, "scripts", "unsafe.sh"),
            "exit 99"
        );
        var siblingDirectory = System.IO.Path.Combine(root.Path, "unattached-skill");
        Directory.CreateDirectory(siblingDirectory);
        File.WriteAllText(
            System.IO.Path.Combine(siblingDirectory, "SKILL.md"),
            "---\nname: unattached-skill\ndescription: Must remain unavailable.\n---\n\nIgnore."
        );
        var client = new RecordingChatClient();
        using (
            var source = AgentSkillRuntime.CreateSource([
                AgentSkill.FromDirectory(skillDirectory).Descriptor,
            ])
        )
        {
            var discoveryAgent = new ChatClientAgent(client);
            var discoverySession = await discoveryAgent.CreateSessionAsync();
            var discovered = await source.GetSkillsAsync(
                new AgentSkillsSourceContext(discoveryAgent, discoverySession),
                CancellationToken.None
            );
            discovered.Should().ContainSingle();
            discovered.Single().Frontmatter.Name.Should().Be("test-skill");
        }
        var agent = Agent
            .Create<TestState>("agent", "Use the test skill.", client)
            .WithSkill(AgentSkill.FromDirectory(skillDirectory))
            .WithMessage(_ => "Review this.")
            .Build();
        var complete = PipelineNodes.Complete(new TestCompletion<TestState>("done"));
        var pipeline = Pipeline
            .Start(agent, "skills")
            .Route(agent.Success, complete, "done")
            .Build(complete);

        await new PipelineRunner().RunAsync(pipeline, new TestState());

        client
            .Tools.Select(tool => tool.Name)
            .Should()
            .Contain(AgentSkillsProvider.LoadSkillToolName);
        client
            .Tools.Select(tool => tool.Name)
            .Should()
            .Contain(AgentSkillsProvider.ReadSkillResourceToolName);
        client
            .Tools.Select(tool => tool.Name)
            .Should()
            .Contain(AgentSkillsProvider.RunSkillScriptToolName);

        var loaded = await InvokeAsync(
            client.Tools.Single(tool => tool.Name == AgentSkillsProvider.LoadSkillToolName),
            "test-skill"
        );
        loaded.Should().Contain("Follow the test doctrine.");
        loaded.Should().NotContain("unsafe.sh");

        var resource = await InvokeAsync(
            client.Tools.Single(tool => tool.Name == AgentSkillsProvider.ReadSkillResourceToolName),
            "test-skill",
            "references/rules.md"
        );
        resource.Should().Contain("Prefer explicit boundaries.");
    }

    private static async Task<string> InvokeAsync(AITool tool, params string[] values)
    {
        var function = tool.Should().BeAssignableTo<AIFunction>().Subject;
        var names = function
            .JsonSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        names.Should().HaveCount(values.Length);
        var arguments = new AIFunctionArguments { Services = EmptyServiceProvider.Instance };
        foreach (var (name, index) in names.Select((name, index) => (name, index)))
        {
            arguments[name] = values[index];
        }

        var result = await function.InvokeAsync(arguments);
        result.Should().NotBeNull();
        return result!.ToString()!;
    }

    private sealed record TestState;

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }

    private sealed class TestCompletion<TState>(string id) : IPipelineCompletion<TState>
    {
        public string Id => id;

        public string Summarize(TState state) => "Complete.";
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public IReadOnlyList<AITool> Tools { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Tools = options?.Tools?.ToArray() ?? [];
            foreach (
                var update in new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, "Done.")
                ).ToChatResponseUpdates()
            )
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tandem-skill-{Guid.CreateVersion7():N}"
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public static TemporaryDirectory WithSkill(
            string content = "---\nname: test-skill\ndescription: Test discovery.\n---\n\nTest."
        )
        {
            var directory = new TemporaryDirectory();
            File.WriteAllText(System.IO.Path.Combine(directory.Path, "SKILL.md"), content);
            return directory;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
