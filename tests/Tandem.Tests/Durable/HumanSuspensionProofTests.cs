using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Tests.Durable;

[Collection("Durable Task Scheduler")]
public sealed class HumanSuspensionProofTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task PlannerNeedsHuman_SuspendsAndResumes_WithAnswer()
    {
        DtsFixture.EnsureReachable();

        var tandemHome = Path.Combine(
            Path.GetTempPath(),
            "tandem-human-suspend-" + Guid.NewGuid().ToString("N")
        );
        var workspacePath = Path.Combine(tandemHome, "workspace");
        var repositoryPath = Path.Combine(tandemHome, "repository");
        Directory.CreateDirectory(repositoryPath);
        await InitializeRepositoryAsync(repositoryPath);

        try
        {
            // Scripted planner that always emits needs_human.
            // The planner returns structured JSON that ParsePlannerDecision
            // will parse as NeedsHuman.
            var plannerClient = new ScriptedChatClient(
                MakeTextResponse(
                    "{\"decision\":\"NeedsHuman\",\"rationale\":\"Need human input.\","
                        + "\"constraints\":[],\"evidenceUsed\":[\"packet outcome\"],"
                        + "\"humanQuestion\":\"Should I proceed?\"}"
                ),
                MakeTextResponse(
                    "{\"decision\":\"NeedsHuman\",\"rationale\":\"Need confirmation.\","
                        + "\"constraints\":[],\"evidenceUsed\":[\"human answer\"],"
                        + "\"humanQuestion\":\"Confirm once more?\"}"
                )
            );

            var stepsFactory = new DeliveryStepsFactory(
                new AgentRuntime(tandemHome, null),
                _ => plannerClient,
                _ => new ResolvedProfile(
                    "test",
                    "https://test",
                    "test-model",
                    Tandem.Domain.WireApi.Completions,
                    null,
                    128000,
                    4096,
                    80
                ),
                new DeliveryDiffAcquisition(new GitProcess()),
                new WorkspacePreparation(new GitProcess()),
                new GitProcess()
            );

            var delivery = stepsFactory.Create(new PipelineBuildContext());
            var pipeline = TandemWorkflow
                .Start(at: delivery.Planner, name: "delivery-human-resume-proof")
                .Route(
                    on: delivery.Planner.Result.NeedsHuman,
                    to: delivery.HumanQuestion,
                    label: "needs human"
                )
                .Route(on: delivery.Planner.Result.Stop, to: delivery.FailRun, label: "stop")
                .Route(
                    on: delivery.Planner.Result.Unexpected,
                    to: delivery.FailRun,
                    label: "unexpected outcome"
                )
                .Route(
                    from: delivery.HumanQuestion,
                    to: delivery.HumanInput,
                    label: "request human input"
                )
                .Route(
                    from: delivery.HumanInput,
                    to: delivery.ApplyHumanAnswer,
                    label: "answer received"
                )
                .Route(
                    when: message =>
                        message.LatestOutcome?.Payload.GetProperty("sourceBlockId").GetString()
                        == BlockIds.Planner,
                    from: delivery.ApplyHumanAnswer,
                    to: delivery.Planner,
                    label: "answer for planner"
                )
                .Build(delivery.FailRun);
            var workflow = PipelineMafBridge.GetWorkflow(pipeline);

            var packet = new Packet(
                "test-packet",
                repositoryPath,
                "main",
                [new Outcome("outcome", "Do the thing.")],
                [],
                [],
                ""
            );
            var runId = Guid.CreateVersion7();
            var message = new PipelineMessage<DeliveryState>(
                PipelineRuntime.Create(runId),
                DeliveryState.Create(packet, "abc123", workspacePath) with
                {
                    MutationAuthorized = false,
                    PlannerDecision = null,
                }
            );

            // Start with a planner-only edge: we need to reach the planner
            // directly. Build a minimal workflow that goes planner → human-question
            // → HumanInput → apply → planner (loop until resolved).
            // For the proof, we just test the planner → human-question → port
            // → apply → planner path with scripted responses.

            var durableRunId = "human-suspend-" + Guid.NewGuid().ToString("N");

            await using (
                var host = await DurableHost.StartAsync(options => options.AddWorkflow(workflow))
            )
            {
                await host.WorkflowClient.RunAsync(workflow, message, durableRunId);
                for (var i = 0; i < 60 && plannerClient.InvocationCount < 1; i++)
                {
                    await Task.Delay(250, CancellationToken.None);
                }
                plannerClient.InvocationCount.Should().Be(1);
            }

            var answer = new HumanAnswer("Use the existing pattern.");
            var serialized = JsonSerializer.Serialize(answer, _jsonOptions);

            await using var restartedHost = await DurableHost.StartAsync(options =>
                options.AddWorkflow(workflow)
            );
            await restartedHost.DurableTaskClient.RaiseEventAsync(
                durableRunId,
                "HumanInput",
                serialized,
                CancellationToken.None
            );

            for (var i = 0; i < 60 && plannerClient.InvocationCount < 2; i++)
            {
                await Task.Delay(250, CancellationToken.None);
            }

            plannerClient.InvocationCount.Should().Be(2, "resume must reinvoke the planner");
            plannerClient
                .UserMessages.Last()
                .Should()
                .Contain(
                    "Use the existing pattern.",
                    "the restored durable reviewer/planner state must reach the resumed invocation"
                );

            // The workflow should be active again (either pending from the
            // second planner call, or processing).
            // We just verify it didn't fail.
            var completed = await restartedHost.DurableTaskClient.GetInstanceAsync(
                durableRunId,
                getInputsAndOutputs: false,
                CancellationToken.None
            );
            completed.Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(tandemHome))
            {
                Directory.Delete(tandemHome, recursive: true);
            }
        }
    }

    private static ChatResponse MakeTextResponse(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    private static async Task InitializeRepositoryAsync(string repositoryPath)
    {
        var git = new GitProcess();
        await git.RunAsync(
            repositoryPath,
            ["init", "--initial-branch=main"],
            CancellationToken.None
        );
        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "README.md"),
            "# Durable human resume fixture\n"
        );
        await git.RunAsync(repositoryPath, ["add", "README.md"], CancellationToken.None);
        await git.RunAsync(
            repositoryPath,
            [
                "-c",
                "user.name=Tandem Tests",
                "-c",
                "user.email=tandem-tests@localhost",
                "commit",
                "-m",
                "fixture",
            ],
            CancellationToken.None
        );
    }

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int InvocationCount { get; private set; }

        public List<string> UserMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(RecordAndDequeue(messages));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            foreach (var update in RecordAndDequeue(messages).ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        private ChatResponse Dequeue() =>
            _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("ScriptedChatClient exhausted.");

        private ChatResponse RecordAndDequeue(IEnumerable<ChatMessage> messages)
        {
            InvocationCount++;
            UserMessages.Add(
                string.Join(
                    "\n",
                    messages
                        .Where(message => message.Role == ChatRole.User)
                        .SelectMany(message => message.Contents.OfType<TextContent>())
                        .Select(content => content.Text)
                )
            );
            return Dequeue();
        }
    }
}
