using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;
using AdvancedToolEffect = Tandem.Advanced.ToolEffect;
using RuntimeToolEffect = Tandem.Infrastructure.ToolEffect;
using RuntimeToolEvidence = Tandem.Infrastructure.ToolEvidence;

#pragma warning disable MAAI001

namespace Tandem.Tests.Composition;

public sealed class DeliveryPolicyRegressionTests
{
    [Theory]
    [InlineData(AdvancedToolEffect.Read, false)]
    [InlineData(AdvancedToolEffect.LifecycleTransition, false)]
    [InlineData(AdvancedToolEffect.WorkspaceMutation, true)]
    [InlineData(AdvancedToolEffect.Unclassified, true)]
    public async Task ExecutorMutationGate_IsSemanticAndFailClosed(
        AdvancedToolEffect effect,
        bool blocked
    )
    {
        var gate = ExecutorPolicies.CreateMutationGate();
        var context = new AgentMessageContext<DeliveryState>(Guid.NewGuid(), CreateState(), null);

        var result = await gate(
            context,
            new ToolInvocation("tool", effect),
            CancellationToken.None
        );

        (result is ToolInterceptionResult.Blocked).Should().Be(blocked);
    }

    [Fact]
    public async Task ExecutorMutationGate_AllowsMutationAfterAuthorityIsAccepted()
    {
        var gate = ExecutorPolicies.CreateMutationGate();
        var state = CreateState() with { MutationAuthorized = true };
        var context = new AgentMessageContext<DeliveryState>(Guid.NewGuid(), state, null);

        var result = await gate(
            context,
            new ToolInvocation("file_access_write", AdvancedToolEffect.WorkspaceMutation),
            CancellationToken.None
        );

        result.Should().BeNull();
    }

    [Fact]
    public void HarnessToolEffects_ClassifyTheCompletePinnedFileToolSet()
    {
        var registry = new ToolEffectRegistry();

        HarnessToolEffects.Register(registry, includeMutations: true);

        AssertEffect(
            registry,
            FileAccessProvider.ReadFileToolName,
            RuntimeToolEffect.Read,
            RuntimeToolEvidence.RepositoryInspection
        );
        AssertEffect(
            registry,
            FileAccessProvider.LsToolName,
            RuntimeToolEffect.Read,
            RuntimeToolEvidence.RepositoryInspection
        );
        AssertEffect(
            registry,
            FileAccessProvider.GrepToolName,
            RuntimeToolEffect.Read,
            RuntimeToolEvidence.RepositoryInspection
        );
        AssertEffect(
            registry,
            FileAccessProvider.WriteToolName,
            RuntimeToolEffect.WorkspaceMutation
        );
        AssertEffect(
            registry,
            FileAccessProvider.DeleteFileToolName,
            RuntimeToolEffect.WorkspaceMutation
        );
        AssertEffect(
            registry,
            FileAccessProvider.ReplaceToolName,
            RuntimeToolEffect.WorkspaceMutation
        );
        AssertEffect(
            registry,
            FileAccessProvider.ReplaceLinesToolName,
            RuntimeToolEffect.WorkspaceMutation
        );
    }

    [Fact]
    public void CheckpointToolEffects_OmitWorkspaceMutations()
    {
        var registry = new ToolEffectRegistry();

        HarnessToolEffects.Register(registry, includeMutations: false);

        registry.TryGet(FileAccessProvider.ReadFileToolName, out _).Should().BeTrue();
        registry.TryGet(FileAccessProvider.WriteToolName, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExecutorHarness_BlocksClassifiedMutationBeforeToolExecution()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "tandem-authority-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(workspace);
        try
        {
            var client = new ScriptedChatClient(
                ToolCall(
                    "write",
                    FileAccessProvider.WriteToolName,
                    new Dictionary<string, object?>()
                ),
                ToolCall(
                    "report",
                    "submit_report",
                    new Dictionary<string, object?>
                    {
                        ["summary"] = "No mutation was performed.",
                        ["outcomes"] = new[] { "No repository changes." },
                        ["evidence"] = new[] { "Mutation gate rejected the write." },
                    }
                )
            );
            var capabilities = TestDeliveryCapabilities.Create();
            var executor = ExecutorAgent.Create(
                new DeliveryAgentFactory(_ => client, _ => new DeliveryAgentProfile(1000, 100, 80)),
                capabilities.AskPlanner,
                capabilities.SubmitReport,
                capabilities.WriteCheckpoint
            );
            var complete = PipelineNodes.Complete<DeliveryState>("complete");
            var pipeline = Pipeline
                .Start(executor, "executor-authority")
                .Route(executor.Success, complete, "accepted")
                .Build(complete);
            var state = CreateState() with { WorkspacePath = workspace };

            var result = await new PipelineRunner().RunAsync(pipeline, state);

            result.Status.Should().Be(PipelineRunStatus.Succeeded);
            result.State.ExecutorTransition.Should().BeOfType<ExecutorTransition.ReportSubmitted>();
            client.CallCount.Should().Be(2);
            client
                .AdvertisedTools.SelectMany(tools => tools)
                .Should()
                .Contain(FileAccessProvider.WriteToolName);
            Directory.EnumerateFileSystemEntries(workspace).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static void AssertEffect(
        ToolEffectRegistry registry,
        string name,
        RuntimeToolEffect expected,
        RuntimeToolEvidence evidence = RuntimeToolEvidence.None
    )
    {
        registry.TryGet(name, out var actual).Should().BeTrue();
        actual.Effect.Should().Be(expected);
        actual.Evidence.Should().Be(evidence);
    }

    private static DeliveryState CreateState() =>
        DeliveryState.Create(new Packet("test", "/tmp/repo", "main", [], [], [], ""), "", "/tmp");

    private static ChatResponse ToolCall(
        string callId,
        string toolName,
        IDictionary<string, object?> arguments
    ) =>
        new(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, toolName, arguments)]
            )
        )
        {
            FinishReason = ChatFinishReason.ToolCalls,
            ModelId = "test-model",
        };

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }
        public List<IReadOnlyList<string>> AdvertisedTools { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Dequeue());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            AdvertisedTools.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
            foreach (var update in Dequeue().ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        private ChatResponse Dequeue()
        {
            CallCount++;
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("ScriptedChatClient exhausted.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

#pragma warning restore MAAI001
