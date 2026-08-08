using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Infrastructure;

public sealed class StructuredOutputTests
{
    [Fact]
    public async Task InvalidPlannerDecision_IsCorrectedInSameSession_BeforeAuthorizingMutation()
    {
        using var directory = new TemporaryDirectory();
        var client = new ScriptedChatClient(
            Response(
                "{\"decision\":\"Proceed\",\"rationale\":\"Proceed.\","
                    + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                    + "\"humanQuestion\":\"N/A\"}"
            ),
            Response(
                "{\"decision\":\"Proceed\",\"rationale\":\"Proceed.\","
                    + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                    + "\"humanQuestion\":null}"
            )
        );
        var block = CreatePlannerBlock(client);
        var context = CreateContext(directory.Path);

        var output = await block.HandleAsync(
            context,
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.PlannerProceed);
        output.State.MutationAuthorized.Should().BeTrue();
        client.CallCount.Should().Be(2);
        client.Instructions.Should().Contain("# Tandem Delivery Harness");
        client.Instructions.Should().Contain("Return a planner decision.");
        client
            .Instructions!.IndexOf("# Tandem Delivery Harness", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                client.Instructions.IndexOf("Return a planner decision.", StringComparison.Ordinal)
            );
        client
            .Requests[1]
            .SelectMany(message => message.Contents)
            .OfType<TextContent>()
            .Select(content => content.Text)
            .Should()
            .Contain(text => text.Contains("humanQuestion") && text.Contains("must be empty"));
    }

    [Fact]
    public async Task SecondInvalidPlannerDecision_FailsClosedWithoutAuthorizingMutation()
    {
        using var directory = new TemporaryDirectory();
        var invalid = Response(
            "{\"decision\":\"Proceed\",\"rationale\":\"Proceed.\","
                + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                + "\"humanQuestion\":\"N/A\"}"
        );
        var client = new ScriptedChatClient(invalid, invalid);
        var block = CreatePlannerBlock(client);

        var output = await block.HandleAsync(
            CreateContext(directory.Path),
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.State.MutationAuthorized.Should().BeFalse();
        output.LatestOutcome.Payload.GetProperty("rawResponse").GetString().Should().Contain("N/A");
    }

    [Fact]
    public async Task GroundingPolicy_RejectsProceedWithoutToolCallsAndFailsClosedAfterCorrection()
    {
        using var directory = new TemporaryDirectory();
        var validProceed = Response(
            "{\"decision\":\"Proceed\",\"rationale\":\"Looks good.\","
                + "\"constraints\":[],\"evidenceUsed\":[\"executor claim\"],"
                + "\"humanQuestion\":null}"
        );
        var client = new ScriptedChatClient(validProceed, validProceed);
        var policy = StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
            result => result.Outcome?.Kind == OutcomeKinds.PlannerProceed,
            correction: "Inspect the repository before approving."
        );
        var block = CreatePlannerBlock(client, policy);

        var output = await block.HandleAsync(
            CreateContext(directory.Path),
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.State.MutationAuthorized.Should().BeFalse();
        client.CallCount.Should().Be(2);
        client
            .Requests[1]
            .SelectMany(message => message.Contents)
            .OfType<TextContent>()
            .Select(content => content.Text)
            .Should()
            .Contain(text => text.Contains("Inspect the repository before approving."));
    }

    [Fact]
    public async Task GroundingPolicy_AcceptsSuccessfullyCompletedRepositoryToolCall()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "README.md"), "evidence");
        var validProceed = Response(
            "{\"decision\":\"Proceed\",\"rationale\":\"Inspected.\","
                + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                + "\"humanQuestion\":null}"
        );
        var client = new ScriptedChatClient(
            ToolResponse(
                "read-1",
                "file_access_read",
                new Dictionary<string, object?> { ["fileName"] = "README.md" }
            ),
            validProceed
        );
        var policy = StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
            result => result.Outcome?.Kind == OutcomeKinds.PlannerProceed,
            name => name.StartsWith("file_access_read", StringComparison.Ordinal)
        );
        var block = CreatePlannerBlock(client, policy);

        var output = await block.HandleAsync(
            CreateContext(directory.Path),
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.PlannerProceed);
        output.State.MutationAuthorized.Should().BeTrue();
    }

    [Fact]
    public async Task GroundingPolicy_DoesNotCountFailedRepositoryToolCall()
    {
        using var directory = new TemporaryDirectory();
        var validProceed = Response(
            "{\"decision\":\"Proceed\",\"rationale\":\"Assumed.\","
                + "\"constraints\":[],\"evidenceUsed\":[\"missing.md\"],"
                + "\"humanQuestion\":null}"
        );
        var client = new ScriptedChatClient(
            ToolResponse(
                "read-1",
                "file_access_read",
                new Dictionary<string, object?> { ["fileName"] = "missing.md" }
            ),
            validProceed,
            validProceed
        );
        var policy = StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
            result => result.Outcome?.Kind == OutcomeKinds.PlannerProceed,
            name => name.StartsWith("file_access_read", StringComparison.Ordinal)
        );
        var block = CreatePlannerBlock(client, policy);

        var output = await block.HandleAsync(
            CreateContext(directory.Path),
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.State.MutationAuthorized.Should().BeFalse();
    }

    [Fact]
    public void GroundingPolicy_AcceptsConfiguredToolWithoutKnowingTheBlock()
    {
        var parsed = PlannerDecisionPolicy.Parse(
            "{\"decision\":\"Proceed\",\"rationale\":\"Verified.\","
                + "\"constraints\":[],\"evidenceUsed\":[\"src/service.ts\"],"
                + "\"humanQuestion\":null}",
            CreateContext("/tmp").State
        );
        var policy = StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(result =>
            result.Outcome?.Kind == OutcomeKinds.PlannerProceed
        );

        var problems = policy(
            new StructuredOutputAcceptanceObservation<DeliveryState>(
                ToAgentContext(CreateContext("/tmp")),
                parsed,
                new HashSet<string> { "file_access_read" },
                0
            )
        );

        problems.Should().BeEmpty();
    }

    [Fact]
    public void GroundingPolicy_RejectsCallsOutsideConfiguredInspectionSurface()
    {
        var parsed = PlannerDecisionPolicy.Parse(
            "{\"decision\":\"Proceed\",\"rationale\":\"Verified.\","
                + "\"constraints\":[],\"evidenceUsed\":[\"src/service.ts\"],"
                + "\"humanQuestion\":null}",
            CreateContext("/tmp").State
        );
        var policy = StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
            result => result.Outcome?.Kind == OutcomeKinds.PlannerProceed,
            name => name.StartsWith("file_access_read", StringComparison.Ordinal)
        );

        var problems = policy(
            new StructuredOutputAcceptanceObservation<DeliveryState>(
                ToAgentContext(CreateContext("/tmp")),
                parsed,
                new HashSet<string> { "submit_report" },
                0
            )
        );

        problems.Should().ContainSingle();
    }

    [Fact]
    public void GroundingPolicy_CanRejectAcceptedCandidateWithSemanticErrors()
    {
        var parsed = PlannerDecisionPolicy.Parse(
            "{\"decision\":\"Proceed\",\"rationale\":\"Inspect first.\","
                + "\"constraints\":[],\"evidenceUsed\":[],\"humanQuestion\":null}",
            CreateContext("/tmp").State
        );
        var policy = StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(result =>
            result.Candidate is PlannerDecision { Decision: PlannerDecisionValue.Proceed }
        );

        var problems = policy(
            new StructuredOutputAcceptanceObservation<DeliveryState>(
                ToAgentContext(CreateContext("/tmp")),
                parsed,
                new HashSet<string>(),
                0
            )
        );

        parsed.Success.Should().BeFalse();
        problems.Should().ContainSingle();
    }

    [Fact]
    public void ReviewerPolicy_RequiresEveryPacketOutcomeWithEvidence()
    {
        var context = CreateContext("/tmp");

        var missing = ReviewDecisionPolicy.Parse(
            "{\"decision\":\"Accept\",\"summary\":\"The candidate delivers the work.\","
                + "\"outcomes\":[],\"findings\":[],\"humanQuestion\":null}",
            context.State
        );
        var valid = ReviewDecisionPolicy.Parse(
            "{\"decision\":\"Accept\",\"summary\":\"The inspected implementation delivers the packet outcome.\","
                + "\"outcomes\":[{\"outcomeId\":\"outcome\",\"delivered\":true,"
                + "\"evidence\":[\"src/service.ts: implementation\"]}],"
                + "\"findings\":[],\"humanQuestion\":null}",
            context.State
        );

        missing.Success.Should().BeFalse();
        missing.Problems.Should().Contain(problem => problem.Message.Contains("outcome"));
        valid.Success.Should().BeTrue();
        valid.Outcome!.Kind.Should().Be(OutcomeKinds.ReviewAccepted);
    }

    [Fact]
    public void PersistedReviewerHumanAnswer_IsRestoredIntoPromptAndClearedAfterDecision()
    {
        var context = CreateContext("/tmp") with
        {
            State = CreateContext("/tmp").State with
            {
                ReviewerDecision = new ReviewDecision(
                    ReviewDecisionValue.NeedsHuman,
                    "Human decision required",
                    [],
                    [],
                    "Keep public behavior?"
                ),
            },
            LatestOutcome = new BlockOutcome(
                OutcomeKinds.ReviewNeedsHuman,
                BlockIds.Reviewer,
                "Human decision required",
                JsonSerializer.SerializeToElement(new { })
            ),
        };

        var resumed = context with
        {
            State = HumanInteraction.ApplyAnswer(
                context.State,
                new HumanAnswer("Keep public behavior.")
            ),
        };

        var persisted = JsonSerializer.Serialize(resumed);
        resumed = JsonSerializer.Deserialize<PipelineMessage<DeliveryState>>(persisted)!;

        resumed.State.ReviewerHumanAnswer.Should().Be("Keep public behavior.");
        resumed.State.HumanAnswerSourceBlockId.Should().Be(BlockIds.Reviewer);
        DeliveryPrompts
            .BuildReviewerMessage(resumed.State)
            .Should()
            .Contain("Human answer for this review:")
            .And.Contain("Keep public behavior.");

        var decision = ReviewDecisionPolicy.Parse(
            "{\"decision\":\"Accept\",\"summary\":\"The inspected implementation delivers the packet outcome.\","
                + "\"outcomes\":[{\"outcomeId\":\"outcome\",\"delivered\":true,"
                + "\"evidence\":[\"src/service.ts: implementation\"]}],"
                + "\"findings\":[],\"humanQuestion\":null}",
            resumed.State
        );

        decision.Outcome!.UpdatedState!.ReviewerHumanAnswer.Should().BeNull();
        decision.Outcome.UpdatedState.HumanAnswerSourceBlockId.Should().BeNull();
    }

    [Fact]
    public void JsonExtractor_IgnoresBracesInsideStrings()
    {
        var json = StructuredJsonExtractor.Extract("prefix {\"rationale\":\"Use {value}\"} suffix");

        json.Should().Be("{\"rationale\":\"Use {value}\"}");
    }

    private static AgentBlock<DeliveryState> CreatePlannerBlock(
        IChatClient client,
        StructuredOutputAcceptancePolicy<DeliveryState>? acceptance = null
    ) =>
        new(
            new AgentBlockConfig<DeliveryState>(
                BlockIds.Planner,
                "planning",
                "Return a planner decision.",
                [],
                _ => "Review the supplied planner request.",
                state => state.WorkspacePath,
                _ => false,
                StructuredOutput: StructuredOutputDescriptors.Create(
                    PlannerDecisionPolicy.Parse,
                    acceptance
                ),
                ContinueSession: true,
                ImplementationFactory: context =>
                    HarnessAgentImplementation.Create(context, DeliveryHarnessInstructions.Value)
            ),
            client
        );

    private static PipelineMessage<DeliveryState> CreateContext(string workspacePath)
    {
        var packet = new Packet(
            "structured-output",
            workspacePath,
            "main",
            [new Outcome("outcome", "Do the thing.")],
            [],
            [],
            ""
        );
        return new PipelineMessage<DeliveryState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            DeliveryState.Create(packet, "abc123", workspacePath)
        );
    }

    private static AgentMessageContext<DeliveryState> ToAgentContext(
        PipelineMessage<DeliveryState> message
    ) =>
        new(
            message.Runtime.RunId,
            message.State,
            message.LatestOutcome is { } outcome
                ? new AgentMessageOutcome(
                    outcome.Kind,
                    outcome.BlockId,
                    outcome.Summary,
                    outcome.Payload,
                    outcome.Duration
                )
                : null
        );

    private static ChatResponse Response(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    private static ChatResponse ToolResponse(
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
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public string? Instructions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Requests.Add(messages.ToArray());
            Instructions = options?.Instructions;
            CallCount++;
            foreach (var update in _responses.Dequeue().ToChatResponseUpdates())
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
                "tandem-structured-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
