using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Durable;

[Collection("Durable Task Scheduler")]
public sealed class DurablePlannerHandoffTests
{
    [Fact]
    public async Task AgentBlockPlannerOutput_RoutesBackToSessionedExecutor()
    {
        DtsFixture.EnsureReachable();

        var tandemHome = Path.Combine(
            Path.GetTempPath(),
            "tandem-planner-roundtrip-" + Guid.NewGuid().ToString("N")
        );
        var workspacePath = Path.Combine(tandemHome, "workspace");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var executorClient = new ScriptedChatClient(
                MakeTextResponse("I need planner guidance."),
                MakeToolCallResponse(
                    "call-1",
                    "ask_planner",
                    new Dictionary<string, object?>
                    {
                        ["question"] = "May I proceed?",
                        ["proposedApproach"] = "Implement the requested change.",
                        ["evidence"] = new[] { "src/service.ts" },
                    }
                ),
                MakeTextResponse("The planner approved the approach.")
            );
            var plannerClient = new ScriptedChatClient(
                MakeTextResponse(
                    "{\"decision\":\"ProceedWithConstraints\",\"rationale\":\"Proceed.\","
                        + "\"constraints\":[\"Keep it focused.\"],"
                        + "\"evidenceUsed\":[\"src/service.ts\"]}"
                )
            );

            var executor = new AgentBlock<SimpleV1State>(
                new AgentBlockConfig<SimpleV1State>(
                    BlockIds.Executor,
                    "implementation",
                    "Ask the planner for guidance.",
                    ["ask_planner", "submit_report"],
                    _ => "Ask the planner for guidance.",
                    state => state.WorkspacePath,
                    _ => false,
                    TurnPolicy: new AgentTurnPolicy<SimpleV1State>(
                        1,
                        (observation, _) =>
                            ValueTask.FromResult<AgentTurnDirective?>(
                                observation.Message.State.PlannerDecision is null
                                    ? new AgentTurnDirective("Call ask_planner now.", "ask_planner")
                                    : null
                            )
                    )
                ),
                executorClient,
                tandemHome,
                ResolveTandemExePath()
            );
            var planner = new AgentBlock<SimpleV1State>(
                new AgentBlockConfig<SimpleV1State>(
                    BlockIds.Planner,
                    "planning",
                    "Return the planner decision as JSON.",
                    [],
                    _ => "Return the planner decision as JSON.",
                    state => state.WorkspacePath,
                    _ => false,
                    StructuredOutput: ParsePlannerDecision
                ),
                plannerClient,
                tandemHome,
                configureChatOptions: options =>
                    options.ResponseFormat = ChatResponseFormat.ForJsonSchema<PlannerDecision>()
            );

            var executorBinding = executor.BindExecutor();
            var plannerBinding = planner.BindExecutor();
            var workflow = new WorkflowBuilder(executorBinding)
                .WithName("durable-agent-planner-roundtrip")
                .AddEdge<PipelineMessage<SimpleV1State>>(
                    executorBinding,
                    plannerBinding,
                    message => message!.LatestOutcome?.Kind == OutcomeKinds.PlannerRequested
                )
                .AddEdge<PipelineMessage<SimpleV1State>>(
                    plannerBinding,
                    executorBinding,
                    message =>
                        message!.LatestOutcome?.Kind == OutcomeKinds.PlannerProceed
                        || message.LatestOutcome?.Kind == OutcomeKinds.PlannerProceedWithConstraints
                )
                .WithOutputFrom(executorBinding)
                .Build();

            var packet = new Packet(
                "roundtrip",
                "/tmp/repository",
                "main",
                [new Outcome("outcome", "Do the thing.")],
                [],
                [],
                ""
            );
            var message = new PipelineMessage<SimpleV1State>(
                PipelineRuntime.Create(Guid.CreateVersion7()),
                SimpleV1State.Create(packet, "abc123", workspacePath)
            );
            var runId = "agent-planner-roundtrip-" + Guid.NewGuid().ToString("N");

            await using var host = await DurableHost.StartAsync(options =>
                options.AddWorkflow(workflow)
            );
            await host.WorkflowClient.RunAsync(workflow, message, runId);
            var instance = await host.DurableTaskClient.WaitForInstanceCompletionAsync(
                runId,
                getInputsAndOutputs: true,
                CancellationToken.None
            );

            instance.Should().NotBeNull();
            var failure = instance!.FailureDetails;
            instance
                .RuntimeStatus.Should()
                .Be(
                    OrchestrationRuntimeStatus.Completed,
                    failure is null
                        ? null
                        : $"{failure.ErrorType}: {failure.ErrorMessage}\n{failure.StackTrace}\n"
                            + $"Inner: {failure.InnerFailure?.ErrorType}: {failure.InnerFailure?.ErrorMessage}\n"
                            + failure.InnerFailure?.StackTrace
                );
            executorClient.CallCount.Should().Be(3);
            plannerClient.CallCount.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(tandemHome))
            {
                Directory.Delete(tandemHome, recursive: true);
            }
        }
    }

    private static StructuredOutputResult<SimpleV1State> ParsePlannerDecision(
        string assistantText,
        PipelineMessage<SimpleV1State> message
    )
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var decision = JsonSerializer.Deserialize<PlannerDecision>(assistantText, options)!;
        return new StructuredOutputResult<SimpleV1State>(
            new StructuredOutcome<SimpleV1State>(
                OutcomeKinds.PlannerProceedWithConstraints,
                decision.Rationale,
                JsonSerializer.SerializeToElement(decision, options),
                message.State with
                {
                    PlannerDecision = decision,
                    PlannerConstraints = decision.Constraints,
                    MutationAuthorized = true,
                }
            ),
            [],
            assistantText
        );
    }

    private static ChatResponse MakeTextResponse(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

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

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(Dequeue());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (var update in Dequeue().ToChatResponseUpdates())
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        private ChatResponse Dequeue()
        {
            CallCount++;
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("ScriptedChatClient exhausted.");
        }
    }

    private static string ResolveTandemExePath()
    {
        var candidate = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "Tandem",
                "bin",
                "Debug",
                "net10.0",
                "Tandem"
            )
        );
        return File.Exists(candidate)
            ? candidate
            : throw new FileNotFoundException("Could not locate the Tandem executable.");
    }
}
