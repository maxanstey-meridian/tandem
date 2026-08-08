using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure;
using RuntimeToolEffect = Tandem.Infrastructure.ToolEffect;
using RuntimeToolEvidence = Tandem.Infrastructure.ToolEvidence;

#pragma warning disable MAAI001

namespace Tandem.Tests.Composition;

public sealed class DeliveryPolicyRegressionTests
{
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
                new DeliveryAgentFactory(
                    _ => client,
                    _ => new DeliveryAgentProfile(1000, 100, 80),
                    new FakeDeliveryRecordSink()
                ),
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

    [Fact]
    public async Task MultipleToolCalls_UseTheGateSnapshotFromRequestStart()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "tandem-gate-snapshot-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(workspace);
        try
        {
            var client = new ScriptedChatClient(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                "write-1",
                                FileAccessProvider.WriteToolName,
                                new Dictionary<string, object?>()
                            ),
                            new FunctionCallContent(
                                "write-2",
                                FileAccessProvider.WriteToolName,
                                new Dictionary<string, object?>()
                            ),
                        ]
                    )
                )
                {
                    FinishReason = ChatFinishReason.ToolCalls,
                    ModelId = "test-model",
                },
                ToolCall(
                    "report",
                    "submit_report",
                    new Dictionary<string, object?>
                    {
                        ["summary"] = "Both writes remained blocked.",
                        ["outcomes"] = new[] { "No repository changes." },
                        ["evidence"] = new[] { "Both calls used the closed snapshot." },
                    }
                )
            );
            var capabilities = TestDeliveryCapabilities.Create();
            var executor = ExecutorAgent.Create(
                new DeliveryAgentFactory(
                    _ => client,
                    _ => new DeliveryAgentProfile(1000, 100, 80),
                    new FakeDeliveryRecordSink()
                ),
                capabilities.AskPlanner,
                capabilities.SubmitReport,
                capabilities.WriteCheckpoint
            );
            var complete = PipelineNodes.Complete<DeliveryState>("complete");
            var pipeline = Pipeline
                .Start(executor, "executor-snapshot")
                .Route(executor.Success, complete, "accepted")
                .Build(complete);

            var result = await new PipelineRunner().RunAsync(
                pipeline,
                CreateState() with
                {
                    WorkspacePath = workspace,
                }
            );

            result.Status.Should().Be(PipelineRunStatus.Succeeded);
            client.CallCount.Should().Be(2);
            Directory.EnumerateFileSystemEntries(workspace).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ReviewerRequest_IncludesDurableContextAndCandidateDiff()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "tandem-review-context-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(workspace);
        try
        {
            var git = new GitProcess();
            await RunGitAsync(git, workspace, "init");
            await RunGitAsync(git, workspace, "config", "user.email", "test@example.com");
            await RunGitAsync(git, workspace, "config", "user.name", "Tandem Test");
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "base\n");
            await RunGitAsync(git, workspace, "add", "README.md");
            await RunGitAsync(git, workspace, "commit", "-m", "base");
            var baseSha = (await RunGitAsync(git, workspace, "rev-parse", "HEAD")).Stdout.Trim();
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "candidate\n");
            await RunGitAsync(git, workspace, "commit", "-am", "candidate");
            var candidateSha = (
                await RunGitAsync(git, workspace, "rev-parse", "HEAD")
            ).Stdout.Trim();
            var client = new ScriptedChatClient(
                TextResponse(
                    "{\"decision\":\"NeedsHuman\",\"summary\":\"A human decision is required.\","
                        + "\"outcomes\":[{\"outcomeId\":\"outcome\",\"delivered\":true,"
                        + "\"evidence\":[\"README.md changed\"]}],\"findings\":[],"
                        + "\"humanQuestion\":\"Should this candidate be accepted?\"}"
                )
            );
            var records = new FakeDeliveryRecordSink();
            var reviewer = ReviewerAgent.Create(
                new DeliveryAgentFactory(
                    _ => client,
                    _ => new DeliveryAgentProfile(1000, 100, 80),
                    records
                ),
                new DeliveryDiffAcquisition(git)
            );
            var pipeline = Pipeline.Start(reviewer, "review-context").Build(reviewer);
            var state = DeliveryState.Create(
                new Packet(
                    "Review context",
                    workspace,
                    baseSha,
                    [new Outcome("outcome", "Change README.")],
                    [],
                    [],
                    ""
                ),
                baseSha,
                workspace
            ) with
            {
                CandidateSha = candidateSha,
            };

            await new PipelineRunner().RunAsync(pipeline, state);

            var request = client.Requests.Should().ContainSingle().Which.Last().Text;
            request.Should().Contain("<durable-delivery-context>");
            request.Should().Contain("Changed files:").And.Contain("README.md");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void GateLatches_AreRunIsolatedAndIndependentFromPlannerAuthority()
    {
        var first = PipelineRuntime
            .Create(Guid.CreateVersion7())
            .WithGateLatch("executor", "checkpoint-required");
        var second = PipelineRuntime.Create(Guid.CreateVersion7());
        var authorized = CreateState() with { MutationAuthorized = true };

        first.IsGateLatched("executor", "checkpoint-required").Should().BeTrue();
        second.IsGateLatched("executor", "checkpoint-required").Should().BeFalse();
        authorized.MutationAuthorized.Should().BeTrue();
        first.WithoutGateLatch("executor", "checkpoint-required").GateLatches.Should().BeEmpty();
        authorized.MutationAuthorized.Should().BeTrue();
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

    private static ChatResponse TextResponse(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    private static async Task<GitResult> RunGitAsync(
        GitProcess git,
        string workspace,
        params string[] arguments
    )
    {
        var result = await git.RunAsync(workspace, arguments, CancellationToken.None);
        result.ExitCode.Should().Be(0, result.Stderr);
        return result;
    }

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }
        public List<IReadOnlyList<string>> AdvertisedTools { get; } = [];
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

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
            Requests.Add(messages.ToArray());
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
