using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Tandem.Tests.Durable;

/// <summary>
/// F-D: typed RequestPort suspension/resumption and Harness session persistence.
/// </summary>
[Collection("Durable Task Scheduler")]
public sealed class FitGateDTests
{
    [Fact]
    public async Task RequestPort_SurvivesHostRestartAndConsumesTypedResponse()
    {
        DtsFixture.EnsureReachable();

        using var runDirectory = new TemporaryDirectory();
        var answerPath = Path.Combine(runDirectory.Path, "answer.txt");
        var workflow = BuildHumanInputWorkflow(answerPath, "fit-d-human-input");
        var runId = "fit-d-human-input-" + Guid.NewGuid().ToString("N");

        DurableWorkflowWaitingForInputEvent? waitingEvent = null;
        await using (
            var firstHost = await DurableHost.StartAsync(options => options.AddWorkflow(workflow))
        )
        {
            await firstHost.WorkflowClient.RunAsync(workflow, "Need approval", runId);

            var pending = await WaitForPendingRequestAsync(
                firstHost.DurableTaskClient,
                runId,
                "HumanInput"
            );
            var question = JsonSerializer.Deserialize<HumanQuestion>(
                pending.Input,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            waitingEvent = new DurableWorkflowWaitingForInputEvent(
                pending.Input,
                RequestPort.Create<HumanQuestion, HumanAnswer>("HumanInput")
            );
            question.Should().NotBeNull();
            question!.Prompt.Should().Be("Need approval");
        }

        waitingEvent.Should().NotBeNull("the workflow must suspend at the request port");
        waitingEvent!.RequestPort.Id.Should().Be("HumanInput");
        waitingEvent.GetInputAs<HumanQuestion>()!.Prompt.Should().Be("Need approval");

        var restartedWorkflow = BuildHumanInputWorkflow(answerPath, "fit-d-human-input");
        await using var restartedHost = await DurableHost.StartAsync(options =>
            options.AddWorkflow(restartedWorkflow)
        );

        var existingRun = await restartedHost.DurableTaskClient.GetInstanceAsync(runId);
        existingRun.Should().NotBeNull("the pending durable run must survive host restart");

        var serializedAnswer = JsonSerializer.Serialize(
            new HumanAnswer(true, "Approved after restart")
        );
        await restartedHost.DurableTaskClient.RaiseEventAsync(
            runId,
            "HumanInput",
            serializedAnswer,
            CancellationToken.None
        );

        var completed = await restartedHost.DurableTaskClient.WaitForInstanceCompletionAsync(
            runId,
            getInputsAndOutputs: true,
            CancellationToken.None
        );
        completed.Should().NotBeNull();
        completed!
            .RuntimeStatus.Should()
            .Be(Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Completed);
        File.ReadAllText(answerPath).Should().Be("Approved after restart");
    }

    [Fact]
    public async Task HarnessSession_CanBeSerializedAfterToolTurnAndContinued()
    {
        using var runDirectory = new TemporaryDirectory();
        var firstClient = new ScriptedChatClient(
            MakeToolCallResponse(
                "call-1",
                "file_access_write",
                new Dictionary<string, object?>
                {
                    ["fileName"] = "first.txt",
                    ["content"] = "created during the first turn",
                }
            ),
            MakeTextResponse("first turn complete")
        );
        var firstAgent = CreateHarnessAgent(firstClient, runDirectory.Path);
        var session = await firstAgent.CreateSessionAsync();

        await DrainAsync(
            firstAgent.RunStreamingAsync(
                "first-turn",
                session,
                cancellationToken: CancellationToken.None
            )
        );

        File.ReadAllText(Path.Combine(runDirectory.Path, "first.txt"))
            .Should()
            .Be("created during the first turn");

        var serializedSession = await firstAgent.SerializeSessionAsync(session);
        serializedSession.ValueKind.Should().NotBe(JsonValueKind.Null);

        var secondClient = new ScriptedChatClient(MakeTextResponse("continued turn"));
        var secondAgent = CreateHarnessAgent(secondClient, runDirectory.Path);
        var restoredSession = await secondAgent.DeserializeSessionAsync(serializedSession);

        await DrainAsync(
            secondAgent.RunStreamingAsync(
                "second-turn",
                restoredSession,
                cancellationToken: CancellationToken.None
            )
        );

        secondClient
            .Requests.SelectMany(messages => messages)
            .SelectMany(message => message.Contents)
            .OfType<TextContent>()
            .Select(content => content.Text)
            .Should()
            .Contain("first-turn");
    }

    private static Workflow BuildHumanInputWorkflow(string answerPath, string workflowName)
    {
        var createQuestion = new FunctionExecutor<string, HumanQuestion>(
            "create-question",
            (input, context, cancellationToken) => ValueTask.FromResult(new HumanQuestion(input))
        );
        var humanInput = RequestPort.Create<HumanQuestion, HumanAnswer>("HumanInput");
        var consumeAnswer = new FunctionExecutor<HumanAnswer>(
            "consume-answer",
            (answer, context, cancellationToken) =>
            {
                File.WriteAllText(answerPath, answer.Comments);
                return ValueTask.CompletedTask;
            }
        );

        return new WorkflowBuilder(createQuestion)
            .WithName(workflowName)
            .AddEdge(createQuestion, humanInput)
            .AddEdge(humanInput, consumeAnswer)
            .Build();
    }

    private static HarnessAgent CreateHarnessAgent(IChatClient client, string workspacePath)
    {
        return new HarnessAgent(
            client,
            new HarnessAgentOptions
            {
                Id = "fit-gate-agent",
                Name = "Fit Gate Agent",
                HarnessInstructions = "",
                ChatOptions = new ChatOptions
                {
                    Instructions = "Use the file tools when asked, then report the result.",
                },
                DisableFileMemory = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableAgentSkillsProvider = true,
                DisableWebSearch = true,
                DisableToolAutoApproval = true,
                DisableOpenTelemetry = true,
                DisableCompaction = true,
                MaximumIterationsPerRequest = 10,
                FileAccessStore = new FileSystemAgentFileStore(workspacePath),
                FileAccessProviderOptions = new FileAccessProviderOptions
                {
                    DisableReadOnlyToolApproval = true,
                    DisableWriteToolApproval = true,
                },
            }
        );
    }

    private static async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> updates)
    {
        await foreach (var _ in updates) { }
    }

    private static async Task<(string EventName, string Input)> WaitForPendingRequestAsync(
        Microsoft.DurableTask.Client.DurableTaskClient client,
        string runId,
        string requestPortId
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var metadata = await client.GetInstanceAsync(runId, true, timeout.Token);
            if (metadata?.SerializedCustomStatus is { } serializedStatus)
            {
                using var document = JsonDocument.Parse(serializedStatus);
                if (TryGetProperty(document.RootElement, "pendingEvents", out var pendingEvents))
                {
                    foreach (var pending in pendingEvents.EnumerateArray())
                    {
                        var eventName = GetStringProperty(pending, "eventName");
                        if (eventName == requestPortId)
                        {
                            return (eventName, GetStringProperty(pending, "input"));
                        }
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value)
        || element.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out value);

    private static string GetStringProperty(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value)
            ? value.GetString() ?? string.Empty
            : throw new InvalidOperationException(
                $"Durable status is missing '{name}' for a pending request."
            );

    private static ChatResponse MakeToolCallResponse(
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

    private static ChatResponse MakeTextResponse(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public ConcurrentQueue<IReadOnlyList<ChatMessage>> Requests { get; } = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Enqueue(messages.ToArray());
            return Task.FromResult(Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Requests.Enqueue(messages.ToArray());
            var response = Dequeue();
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        private ChatResponse Dequeue()
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("ScriptedChatClient exhausted.");
            }

            return _responses.Dequeue();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed record HumanQuestion(string Prompt);

    private sealed record HumanAnswer(bool Approved, string Comments);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-fit-d-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for a test fixture.
            }
        }
    }
}

#pragma warning restore MAAI001
