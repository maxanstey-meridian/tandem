using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Infrastructure;

public sealed class StructuredOutputTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task TypedOutputAcceptance_DecoratesCoreValidationAndStateApplication()
    {
        var client = new ScriptedChatClient(
            Response(
                "{\"decision\":\"NeedsHuman\",\"rationale\":\"A product decision is required.\","
                    + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                    + "\"humanQuestion\":\"Which behavior?\"}"
            )
        );
        var agent = Agent
            .Create<DeliveryState>("planner", "Plan.", client)
            .WithMessage(_ => "Decide.")
            .WithOutput(
                new PlannerDecisionOutput(),
                (state, decision) => state.RecordPlannerDecision(decision)
            )
            .RequireOutputAcceptance(PlannerPolicies.RepositoryGrounded())
            .Build();
        var complete = PipelineNodes.Complete<DeliveryState>("complete");
        var pipeline = Pipeline
            .Start(agent, "typed-acceptance")
            .Route(agent.Success, complete, "accepted")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(pipeline, CreateContext("/tmp").State);

        result.Status.Should().Be(PipelineRunStatus.Succeeded);
        result.State.PlannerDecision!.Decision.Should().Be(PlannerDecisionValue.NeedsHuman);
        result.Outcome!.Kind.Should().Be(StandardOutcomeKinds.Success);
    }

    [Fact]
    public async Task AsyncOutputAcceptance_CompletesBeforeMappedStateIsEmitted()
    {
        var accepted = false;
        string? acceptedOutputId = null;
        var client = new ScriptedChatClient(
            Response(
                "{\"decision\":\"Proceed\",\"rationale\":\"Proceed.\","
                    + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                    + "\"humanQuestion\":null}"
            )
        );
        var agent = Agent
            .Create<DeliveryState>("planner", "Plan.", client)
            .WithMessage(_ => "Decide.")
            .WithOutput(
                new PlannerDecisionOutput(),
                (state, decision) => state.RecordPlannerDecision(decision)
            )
            .WithOutputAcceptance<DeliveryState, PlannerDecision>(
                (observation, _) =>
                {
                    observation.Context.State.MutationAuthorized.Should().BeFalse();
                    observation.Output.Decision.Should().Be(PlannerDecisionValue.Proceed);
                    acceptedOutputId = observation.AcceptedOutputId;
                    accepted = true;
                    return ValueTask.CompletedTask;
                }
            )
            .Build();
        var complete = PipelineNodes.Complete<DeliveryState>("complete");
        var pipeline = Pipeline
            .Start(agent, "async-output-acceptance")
            .Route(agent.Success, complete, "accepted")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(pipeline, CreateContext("/tmp").State);

        accepted.Should().BeTrue();
        acceptedOutputId.Should().MatchRegex("^[0-9a-f]{32}--planner--1--output$");
        result.State.MutationAuthorized.Should().BeTrue();
    }

    [Fact]
    public async Task AsyncOutputAcceptanceFailure_PreventsMappedStateAndRouting()
    {
        var mappings = 0;
        var client = new ScriptedChatClient(
            Response(
                "{\"decision\":\"Proceed\",\"rationale\":\"Proceed.\","
                    + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                    + "\"humanQuestion\":null}"
            )
        );
        var agent = Agent
            .Create<DeliveryState>("planner", "Plan.", client)
            .WithMessage(_ => "Decide.")
            .WithOutput(
                new PlannerDecisionOutput(),
                (state, decision) =>
                {
                    Interlocked.Increment(ref mappings);
                    return state.RecordPlannerDecision(decision);
                }
            )
            .WithOutputAcceptance<DeliveryState, PlannerDecision>(
                (_, _) => ValueTask.FromException(new IOException("Ledger unavailable."))
            )
            .Build();
        var complete = PipelineNodes.Complete<DeliveryState>("complete");
        var pipeline = Pipeline
            .Start(agent, "failed-output-acceptance")
            .Route(agent.Success, complete, "must not route")
            .Build(complete);

        var run = async () =>
            await new PipelineRunner().RunAsync(pipeline, CreateContext("/tmp").State);

        var exception = await run.Should().ThrowAsync<PipelineRunException>();
        exception.Which.InnerException.Should().BeOfType<IOException>();
        mappings.Should().Be(0);
    }

    [Fact]
    public async Task SynchronousRejection_DoesNotMap_AndCorrectedOutputMapsExactlyOnce()
    {
        var mappings = 0;
        var client = new ScriptedChatClient(Response("{\"value\":1}"), Response("{\"value\":2}"));
        var agent = Agent
            .Create<MappingState>("agent", "Decide.", client)
            .WithMessage(_ => "Return a value.")
            .WithOutput(
                new MappingOutputDefinition(),
                (state, output) =>
                {
                    Interlocked.Increment(ref mappings);
                    return state with { Value = output.Value };
                }
            )
            .RequireOutputAcceptance<MappingState, MappingOutput>(observation =>
                observation.Attempt == 0
                    ? [new StructuredOutputProblem("value", "Use the corrected value.")]
                    : []
            )
            .Build();
        var complete = PipelineNodes.Complete<MappingState>("complete");
        var pipeline = Pipeline
            .Start(agent, "synchronous-output-rejection")
            .Route(agent.Success, complete, "accepted")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(pipeline, new MappingState(0));

        result.State.Value.Should().Be(2);
        mappings.Should().Be(1);
        client.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task PersistentPipeline_ObservesAcceptedTypedOutputPayload()
    {
        var observations = new List<PipelineObservation>();
        var agent = Agent
            .Create<MappingState>(
                "agent",
                "Decide.",
                new ScriptedChatClient(Response("{\"value\":7}"))
            )
            .WithMessage(_ => "Return a value.")
            .WithOutput(
                new MappingOutputDefinition(),
                (state, output) => state with { Value = output.Value }
            )
            .Build();
        var pipeline = Pipeline.Start(agent, "persistent-output").Persist().Build(agent);

        await new PipelineRunner().RunAsync(
            pipeline,
            new MappingState(0),
            new PipelineRunOptions(
                Observer: new InlinePersistenceObserver(observation =>
                    observations.Add(observation)
                )
            )
        );

        var accepted = observations
            .OfType<PipelineStructuredOutputAccepted>()
            .Should()
            .ContainSingle()
            .Which;
        accepted.OutputType.Should().Be(typeof(MappingOutput).FullName);
        accepted.Payload!.Value.GetProperty("value").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task JournalFailure_DoesNotInvokeOutputMapper()
    {
        var mappings = 0;
        var agent = Agent
            .Create<MappingState>(
                "agent",
                "Decide.",
                new ScriptedChatClient(Response("{\"value\":1}"))
            )
            .WithMessage(_ => "Return a value.")
            .WithOutput(
                new MappingOutputDefinition(),
                (state, output) =>
                {
                    Interlocked.Increment(ref mappings);
                    return state with { Value = output.Value };
                }
            )
            .Build();
        var complete = PipelineNodes.Complete<MappingState>("complete");
        var pipeline = Pipeline
            .Start(agent, "journal-output-failure")
            .Route(agent.Success, complete, "accepted")
            .Build(complete);
        var unitOfWork = new CountingAcceptanceUnitOfWork();
        var options = new PipelineRunOptions(
            Observer: new FailingOutputJournalObserver()
        ).WithAcceptanceUnitOfWork(unitOfWork);

        var run = async () =>
            await new PipelineRunner().RunAsync(pipeline, new MappingState(0), options);

        await run.Should().ThrowAsync<PipelineRunException>();
        unitOfWork.ExecutionCount.Should().Be(1);
        mappings.Should().Be(0);
    }

    [Fact]
    public async Task ContextualValidation_RunsAfterIntrinsic_AndBeforeAcceptanceAndMapping()
    {
        var order = new List<string>();
        var definition = new OrderedMappingOutputDefinition(order);
        var agent = Agent
            .Create<MappingState>(
                "agent",
                "Decide.",
                new ScriptedChatClient(Response("{\"value\":1}"), Response("{\"value\":1}"))
            )
            .WithMessage(_ => "Return a value.")
            .WithOutput(
                definition,
                (state, output) =>
                {
                    order.Add("map");
                    return state with { Value = output.Value };
                }
            )
            .WithOutputAcceptance<MappingState, MappingOutput>(
                (_, _) =>
                {
                    order.Add("accept");
                    return ValueTask.CompletedTask;
                }
            )
            .Build();
        var pipeline = Pipeline.Start(agent, "contextual-output-validation").Build(agent);

        var result = await new PipelineRunner().RunAsync(pipeline, new MappingState(0));

        result.Status.Should().Be(PipelineRunStatus.Failed);
        order.Should().Equal("intrinsic", "contextual", "intrinsic", "contextual");
        order.Should().NotContain("accept").And.NotContain("map");
    }

    [Fact]
    public async Task InvalidOutput_DoesNotReachSynchronousAcceptance()
    {
        var acceptances = 0;
        var validationOrder = new List<string>();
        var agent = Agent
            .Create<MappingState>(
                "agent",
                "Decide.",
                new ScriptedChatClient(Response("{\"value\":0}"), Response("{\"value\":0}"))
            )
            .WithMessage(_ => "Return a value.")
            .WithOutput(
                new OrderedMappingOutputDefinition(validationOrder),
                (state, output) => state with { Value = output.Value }
            )
            .RequireOutputAcceptance<MappingState, MappingOutput>(_ =>
            {
                Interlocked.Increment(ref acceptances);
                return [];
            })
            .Build();
        var pipeline = Pipeline.Start(agent, "invalid-output-acceptance").Build(agent);

        var result = await new PipelineRunner().RunAsync(pipeline, new MappingState(0));

        result.Status.Should().Be(PipelineRunStatus.Failed);
        acceptances.Should().Be(0);
    }

    [Fact]
    public void TypedOutputAcceptance_RejectsMismatchedConfiguredOutputType()
    {
        var builder = Agent
            .Create<DeliveryState>("planner", "Plan.", new ScriptedChatClient())
            .WithMessage(_ => "Decide.")
            .WithOutput(
                new PlannerDecisionOutput(),
                (state, decision) => state.RecordPlannerDecision(decision)
            );

        var act = () => builder.RequireOutputAcceptance(ReviewerPolicies.RepositoryGrounded());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ReviewDecision*PlannerDecision*");
    }

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

        output.LatestOutcome!.Kind.Should().Be(StandardOutcomeKinds.Success);
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
        var policy = PlannerPolicies.RepositoryGrounded();
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
            .Contain(text => text.Contains("require repository inspection"));
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
        var policy = PlannerPolicies.RepositoryGrounded();
        var block = CreatePlannerBlock(client, policy);

        var output = await block.HandleAsync(
            CreateContext(directory.Path),
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be(StandardOutcomeKinds.Success);
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
        var policy = PlannerPolicies.RepositoryGrounded();
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
    public void ReviewerPolicy_RequiresEveryPacketOutcomeWithEvidence()
    {
        var context = CreateContext("/tmp");

        var missing = ParseReviewDecision(
            "{\"decision\":\"Accept\",\"summary\":\"The candidate delivers the work.\","
                + "\"outcomes\":[],\"findings\":[],\"humanQuestion\":null}",
            context.State
        );
        var valid = ParseReviewDecision(
            "{\"decision\":\"Accept\",\"summary\":\"The inspected implementation delivers the packet outcome.\","
                + "\"outcomes\":[{\"outcomeId\":\"outcome\",\"delivered\":true,"
                + "\"evidence\":[\"src/service.ts: implementation\"]}],"
                + "\"findings\":[],\"humanQuestion\":null}",
            context.State
        );

        missing.Success.Should().BeFalse();
        missing.Problems.Should().Contain(problem => problem.Message.Contains("outcome"));
        valid.Success.Should().BeTrue();
        valid.Outcome!.Kind.Should().Be(StandardOutcomeKinds.Success);
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
                StandardOutcomeKinds.Success,
                DeliveryIds.Reviewer,
                "Human decision required",
                JsonSerializer.SerializeToElement(new { })
            ),
        };

        var resumed = context with
        {
            State = HumanInteraction.ApplyReviewerAnswer(
                context.State,
                new HumanAnswer("Keep public behavior.")
            ),
        };

        var persisted = JsonSerializer.Serialize(resumed);
        resumed = JsonSerializer.Deserialize<PipelineMessage<DeliveryState>>(persisted)!;

        resumed.State.ReviewerHumanAnswer.Should().Be("Keep public behavior.");
        ReviewerPrompts
            .BuildMessage(resumed.State)
            .Should()
            .Contain("Human answer for this review:")
            .And.Contain("Keep public behavior.");

        var decision = ParseReviewDecision(
            "{\"decision\":\"Accept\",\"summary\":\"The inspected implementation delivers the packet outcome.\","
                + "\"outcomes\":[{\"outcomeId\":\"outcome\",\"delivered\":true,"
                + "\"evidence\":[\"src/service.ts: implementation\"]}],"
                + "\"findings\":[],\"humanQuestion\":null}",
            resumed.State
        );

        decision.Outcome!.UpdatedState!.ReviewerHumanAnswer.Should().BeNull();
    }

    [Fact]
    public void JsonExtractor_IgnoresBracesInsideStrings()
    {
        var json = StructuredJsonExtractor.Extract("prefix {\"rationale\":\"Use {value}\"} suffix");

        json.Should().Be("{\"rationale\":\"Use {value}\"}");
    }

    private static AgentBlock<DeliveryState> CreatePlannerBlock(
        IChatClient client,
        OutputAcceptancePolicy<DeliveryState, PlannerDecision>? acceptance = null
    )
    {
        var structuredOutput = StructuredOutputDescriptors.Create<DeliveryState>(
            ParsePlannerDecision
        );
        if (acceptance is not null)
        {
            structuredOutput = structuredOutput with
            {
                Accept = StructuredOutputDescriptors.Accept(acceptance),
            };
        }
        return new AgentBlock<DeliveryState>(
            new AgentBlockConfig<DeliveryState>(
                DeliveryIds.Planner,
                "planning",
                "Return a planner decision.",
                [],
                _ => "Review the supplied planner request.",
                state => state.WorkspacePath,
                _ => false,
                StructuredOutput: structuredOutput,
                ContinueSession: true,
                ImplementationFactory: context =>
                    HarnessAgentImplementation.Create(context, DeliveryHarnessInstructions.Value)
            ),
            client
        );
    }

    private static StructuredOutputResult<DeliveryState> ParsePlannerDecision(
        string response,
        DeliveryState state
    ) =>
        StructuredOutputPolicy.Parse(
            response,
            state,
            _jsonOptions,
            new PlannerDecisionValidator(),
            (decision, current) =>
                new StructuredOutcome<DeliveryState>(
                    StandardOutcomeKinds.Success,
                    "Succeeded",
                    JsonSerializer.SerializeToElement(decision, _jsonOptions),
                    current.RecordPlannerDecision(decision)
                )
        );

    private static StructuredOutputResult<DeliveryState> ParseReviewDecision(
        string response,
        DeliveryState state
    ) =>
        StructuredOutputPolicy.Parse(
            response,
            state,
            _jsonOptions,
            new ReviewDecisionValidator(state.Packet.Outcomes.Select(outcome => outcome.Id)),
            (decision, current) =>
                new StructuredOutcome<DeliveryState>(
                    StandardOutcomeKinds.Success,
                    "Succeeded",
                    JsonSerializer.SerializeToElement(decision, _jsonOptions),
                    current.RecordReviewDecision(decision)
                )
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

    private sealed record MappingState(int Value);

    private sealed record MappingOutput(int Value);

    private sealed class MappingOutputDefinition
        : IAgentOutputDefinition<MappingState, MappingOutput>
    {
        public string Instructions => "Return a value.";
        public IValidator<MappingOutput> Validator { get; } = new InlineValidator<MappingOutput>();
    }

    private sealed class OrderedMappingOutputDefinition(List<string> order)
        : IAgentOutputDefinition<MappingState, MappingOutput>
    {
        public string Instructions => "Return a value.";
        public IValidator<MappingOutput> Validator { get; } = CreateValidator("intrinsic", order);

        public IValidator<MappingOutput> ValidatorFor(MappingState state) =>
            CreateValidator("contextual", order, fail: true);

        private static IValidator<MappingOutput> CreateValidator(
            string name,
            List<string> order,
            bool fail = false
        )
        {
            var validator = new InlineValidator<MappingOutput>();
            validator
                .RuleFor(output => output.Value)
                .Custom(
                    (_, context) =>
                    {
                        order.Add(name);
                        if (fail)
                        {
                            context.AddFailure("Context rejected the value.");
                        }
                    }
                );
            return validator;
        }
    }

    private sealed class FailingOutputJournalObserver : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        ) =>
            observation is PipelineStructuredOutputAccepted
                ? ValueTask.FromException(new IOException("Journal failed."))
                : ValueTask.CompletedTask;
    }

    private sealed class InlinePersistenceObserver(Action<PipelineObservation> observe)
        : IPipelinePersistenceObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            observe(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingAcceptanceUnitOfWork
        : Tandem.Advanced.IPipelineAcceptanceUnitOfWork
    {
        public int ExecutionCount { get; private set; }

        public ValueTask<T> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken
        )
        {
            ExecutionCount++;
            return operation(cancellationToken);
        }
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
