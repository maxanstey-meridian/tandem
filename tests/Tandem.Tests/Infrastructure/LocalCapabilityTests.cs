using FluentAssertions;
using FluentValidation;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;
using Tandem.Ledger;

namespace Tandem.Tests.Infrastructure;

public sealed class LocalCapabilityTests
{
    [Fact]
    public void Capability_AdvertisesFlatTypedSchema()
    {
        var capability = CreateCapability();
        var function = capability.Bind(
            new CapabilityInvocationState<TestState>(
                Guid.CreateVersion7(),
                "agent",
                "invocation-1",
                new TestState(0)
            )
        );

        function.Name.Should().Be("increment");
        function
            .JsonSchema.GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("amount");
        function
            .JsonSchema.GetProperty("properties")
            .TryGetProperty("request", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task RunLedger_AutomaticallyAdvertisesGenericReadAndSearchTools()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var runId = Guid.CreateVersion7();
        var store = new SqliteLedgerStore(Path.Combine(directory, "ledger.sqlite3"));
        await store.InitializeAsync();
        await store.CreateRunAsync(runId, "test");
        var client = new TestChatClient(
            ToolCall("accepted", "increment", new Dictionary<string, object?> { ["amount"] = 1 })
        );
        var message = new PipelineMessage<TestState>(
            PipelineRuntime.Create(runId),
            new TestState(0)
        )
        {
            RunContext = new PipelineRunContext(runId, null, ledger: store.ForRun(runId)),
        };

        await CreateBlock(client, CreateCapability()).ExecuteAsync(message, CancellationToken.None);

        client
            .AdvertisedTools.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(["increment", "read_ledger", "read_ledger_entry", "search_ledger"]);
        var descriptions = client
            .Options.Should()
            .ContainSingle()
            .Subject!.Tools!.ToDictionary(tool => tool.Name, tool => tool.Description);
        descriptions["read_ledger"]
            .Should()
            .Contain("accepted durable lifecycle history")
            .And.Contain("verified against the current repository before reliance");
        descriptions["search_ledger"]
            .Should()
            .Contain("accepted durable lifecycle history")
            .And.Contain("does not establish current repository or implementation state");
    }

    [Fact]
    public async Task InvalidCall_ReturnsProblems_ThenAcceptsCorrectedCallInSameSession()
    {
        var client = new TestChatClient(
            ToolCall("invalid", "increment", new Dictionary<string, object?> { ["amount"] = 0 }),
            ToolCall("corrected", "increment", new Dictionary<string, object?> { ["amount"] = 2 })
        );

        var output = await RunBlockAsync(CreateBlock(client, CreateCapability()));

        output.State.Count.Should().Be(2);
        output.LatestOutcome!.Kind.Should().Be(CapabilityKind("increment"));
        client.CallCount.Should().Be(2);
        client
            .Requests[1]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Should()
            .ContainSingle(result => result.CallId == "invalid")
            .Which.Result!.ToString()
            .Should()
            .Contain("invalid increment call");
    }

    [Fact]
    public async Task WrongShape_ReturnsToolError_ThenAcceptsCorrectedCall()
    {
        var client = new TestChatClient(
            ToolCall(
                "wrong-shape",
                "increment",
                new Dictionary<string, object?> { ["unexpected"] = 2 }
            ),
            ToolCall("corrected", "increment", new Dictionary<string, object?> { ["amount"] = 3 })
        );

        var output = await RunBlockAsync(CreateBlock(client, CreateCapability()));

        output.State.Count.Should().Be(3);
        client.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task ContextualValidation_RunsAfterIntrinsic_AndBeforeAcceptanceOrMapping()
    {
        var order = new List<string>();
        var capability = AgentCapabilities
            .Create(
                new OrderedCapabilityDefinition(order),
                (state, request) =>
                {
                    order.Add("map");
                    return state with { Count = state.Count + request.Amount };
                }
            )
            .WithAcceptance(
                (_, _) =>
                {
                    order.Add("accept");
                    return ValueTask.CompletedTask;
                }
            );
        var invocation = new CapabilityInvocationState<TestState>(
            Guid.CreateVersion7(),
            "agent",
            "invocation-1",
            new TestState(0)
        );

        var result = await capability.Bind(invocation).InvokeAsync(Arguments(1));

        IsError(result).Should().BeTrue();
        order.Should().Equal("intrinsic", "contextual");
        invocation.Accepted.Should().BeNull();
    }

    [Fact]
    public async Task AcceptanceFailure_DoesNotTransitionAndAllowsLaterCorrectedCall()
    {
        var attempts = 0;
        var validator = new InlineValidator<IncrementRequest>();
        validator.RuleFor(request => request.Amount).GreaterThan(0);
        var capability = AgentCapabilities
            .Create(
                new TestCapabilityDefinition<TestState, IncrementRequest>(
                    "increment",
                    "Increment the state.",
                    validator,
                    request => $"Incremented by {request.Amount}"
                ),
                (state, request) => state with { Count = state.Count + request.Amount }
            )
            .WithAcceptance(
                (_, _) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        throw new IOException("Persistence failed.");
                    }

                    return ValueTask.CompletedTask;
                }
            );
        var client = new TestChatClient(
            ToolCall("first", "increment", new Dictionary<string, object?> { ["amount"] = 1 }),
            ToolCall("retry", "increment", new Dictionary<string, object?> { ["amount"] = 4 })
        );

        var output = await RunBlockAsync(CreateBlock(client, capability));

        attempts.Should().Be(2);
        output.State.Count.Should().Be(4);
        output.LatestOutcome!.Kind.Should().Be(CapabilityKind("increment"));
    }

    [Fact]
    public async Task ObserverFailureAtCapabilityBoundary_DoesNotCommitStateOrCompleteVisit()
    {
        var runId = Guid.CreateVersion7();
        var capability = CreateCapability();
        var client = new TestChatClient(
            ToolCall("call-1", "increment", new Dictionary<string, object?> { ["amount"] = 1 }),
            ToolCall("call-2", "increment", new Dictionary<string, object?> { ["amount"] = 1 })
        );
        var input = new PipelineMessage<TestState>(PipelineRuntime.Create(runId), new TestState(0))
        {
            RunContext = new PipelineRunContext(runId, new FailingAcceptanceObserver()),
        };

        var execute = async () =>
            await CreateBlock(client, capability).ExecuteAsync(input, CancellationToken.None);

        await execute.Should().ThrowAsync<Exception>();
        input.State.Count.Should().Be(0);
        client.CallCount.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task FirstAcceptedCall_TerminatesTurnBeforeLaterCapability()
    {
        var acceptances = 0;
        var capability = CreateCapability(_ => Interlocked.Increment(ref acceptances));
        var client = new TestChatClient(
            new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "first",
                            "increment",
                            new Dictionary<string, object?> { ["amount"] = 1 }
                        ),
                        new FunctionCallContent(
                            "second",
                            "increment",
                            new Dictionary<string, object?> { ["amount"] = 10 }
                        ),
                        new TextContent("must not continue"),
                    ]
                )
            )
            {
                FinishReason = ChatFinishReason.ToolCalls,
                ModelId = "test-model",
            }
        );

        var output = await RunBlockAsync(CreateBlock(client, capability));

        acceptances.Should().Be(1);
        output.State.Count.Should().Be(1);
        client.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistentStep_ObservesAcceptedCapabilityPayload()
    {
        var observations = new List<PipelineObservation>();
        var observer = new RecordingObserver(observations);
        var runId = Guid.CreateVersion7();
        var input = new PipelineMessage<TestState>(PipelineRuntime.Create(runId), new TestState(0))
        {
            RunContext = new PipelineRunContext(
                runId,
                observer,
                persistentStepIds: new HashSet<string>(StringComparer.Ordinal) { "agent" }
            ),
        };
        var client = new TestChatClient(
            ToolCall("call", "increment", new Dictionary<string, object?> { ["amount"] = 3 })
        );

        await CreateBlock(client, CreateCapability()).ExecuteAsync(input, CancellationToken.None);

        var accepted = observations
            .OfType<PipelineCapabilityAccepted>()
            .Should()
            .ContainSingle()
            .Which;
        accepted.AcceptedCallId.Should().Contain(accepted.CapabilityId);
        accepted.RequestType.Should().Be(typeof(IncrementRequest).FullName);
        accepted.Payload!.Value.GetProperty("amount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task AcceptedCapability_SkipsConfiguredStructuredOutputAndCorrection()
    {
        var validator = new InlineValidator<IncrementRequest>();
        validator.RuleFor(request => request.Amount).GreaterThan(0);
        var client = new TestChatClient(
            ToolCall("accepted", "increment", new Dictionary<string, object?> { ["amount"] = 2 })
        );
        var agent = Agent
            .Create<TestState>("agent", "Choose a result.", client)
            .WithMessage(_ => "Increment the state.")
            .WithOutput(
                new TestOutputDefinition<TestState, IncrementRequest>("", validator),
                (state, request) => state with { Count = state.Count + request.Amount + 100 }
            )
            .WithCapability(CreateCapability())
            .Build();
        var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
        var pipeline = Pipeline
            .Start(agent, "capability-or-output")
            .Route(agent.Success, complete, "completed")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(pipeline, new TestState(0));

        result.State.Count.Should().Be(2);
        result.Status.Should().Be(PipelineRunStatus.Succeeded);
        client.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentCalls_ExecuteOneAcceptanceAndReturnOneConflict()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptances = 0;
        var validator = new InlineValidator<IncrementRequest>();
        validator.RuleFor(request => request.Amount).GreaterThan(0);
        var capability = AgentCapabilities
            .Create(
                new TestCapabilityDefinition<TestState, IncrementRequest>(
                    "increment",
                    "Increment the state.",
                    validator,
                    request => $"Incremented by {request.Amount}"
                ),
                (state, request) => state with { Count = state.Count + request.Amount }
            )
            .WithAcceptance(
                async (_, cancellationToken) =>
                {
                    Interlocked.Increment(ref acceptances);
                    entered.SetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            );
        var invocation = new CapabilityInvocationState<TestState>(
            Guid.CreateVersion7(),
            "agent",
            "invocation-1",
            new TestState(0)
        );
        var function = capability.Bind(invocation);

        var first = function.InvokeAsync(Arguments(1));
        await entered.Task;
        var second = await function.InvokeAsync(Arguments(2));
        release.SetResult();
        await first;

        acceptances.Should().Be(1);
        IsError(second).Should().BeTrue();
        invocation.Accepted!.State.Count.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_ReleasesReservationAndPreservesAcceptedCallIdentity()
    {
        AgentCapabilityAcceptanceContext<TestState, IncrementRequest>? acceptedContext = null;
        var attempt = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validator = new InlineValidator<IncrementRequest>();
        validator.RuleFor(request => request.Amount).GreaterThan(0);
        var capability = AgentCapabilities
            .Create(
                new TestCapabilityDefinition<TestState, IncrementRequest>(
                    "increment",
                    "Increment the state.",
                    validator,
                    request => $"Incremented by {request.Amount}"
                ),
                (state, request) => state with { Count = state.Count + request.Amount }
            )
            .WithAcceptance(
                async (context, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref attempt) == 1)
                    {
                        entered.SetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    acceptedContext = context;
                }
            );
        var runId = Guid.CreateVersion7();
        const string blockId = "agent";
        const string invocationId = "invocation-7";
        var observations = new List<PipelineObservation>();
        var invocation = new CapabilityInvocationState<TestState>(
            runId,
            blockId,
            invocationId,
            new TestState(0),
            new PipelineRunContext(runId, new RecordingObserver(observations))
        );
        var function = capability.Bind(invocation);
        using var cancellation = new CancellationTokenSource();
        var pending = function.InvokeAsync(Arguments(1), cancellation.Token).AsTask();
        await entered.Task;
        cancellation.Cancel();

        var cancelled = async () => await pending;

        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        await function.InvokeAsync(Arguments(3));
        acceptedContext.Should().NotBeNull();
        acceptedContext!.RunId.Should().Be(runId);
        acceptedContext.StepId.Should().Be(blockId);
        acceptedContext.InvocationId.Should().Be(invocationId);
        acceptedContext.CapabilityId.Should().Be(CapabilityKind("increment"));
        observations
            .OfType<PipelineCapabilityAccepted>()
            .Should()
            .ContainSingle()
            .Which.AcceptedCallId.Should()
            .Be(acceptedContext.AcceptedCallId);
        invocation.Accepted!.State.Count.Should().Be(3);
    }

    [Fact]
    public async Task CancellationRequestedAfterAcceptanceCallback_PreventsStateTransition()
    {
        using var cancellation = new CancellationTokenSource();
        var applied = false;
        var capability = AgentCapabilities
            .Create(
                new TestCapabilityDefinition<TestState, IncrementRequest>(
                    "increment",
                    "Increment the state.",
                    new InlineValidator<IncrementRequest>(),
                    request => $"Incremented by {request.Amount}"
                ),
                (state, request) =>
                {
                    applied = true;
                    return state with { Count = state.Count + request.Amount };
                }
            )
            .WithAcceptance(
                (_, _) =>
                {
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }
            );
        var invocation = new CapabilityInvocationState<TestState>(
            Guid.CreateVersion7(),
            "agent",
            "invocation-1",
            new TestState(0)
        );

        var invoke = async () =>
            await capability.Bind(invocation).InvokeAsync(Arguments(1), cancellation.Token);

        await invoke.Should().ThrowAsync<OperationCanceledException>();
        applied.Should().BeFalse();
        invocation.Accepted.Should().BeNull();
        invocation.TryReserve().Should().BeTrue();
    }

    [Fact]
    public void DistinctCapabilitiesWithSameName_AreRejected()
    {
        var first = CreateCapability();
        var second = CreateCapability();
        var builder = Agent
            .Create<TestState>("agent", "Test capabilities.", new TestChatClient())
            .WithMessage(_ => "message")
            .ContinueSession()
            .WithCapability(first);

        var act = () => builder.WithCapability(second);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*multiple capabilities named*");
    }

    [Fact]
    public async Task CheckpointThreshold_ExposesOnlyCheckpointAndClearsSessionAndUsage()
    {
        var validator = new InlineValidator<IncrementRequest>();
        validator.RuleFor(request => request.Amount).GreaterThan(0);
        var checkpoint = AgentCapabilities.Create(
            new TestCapabilityDefinition<TestState, IncrementRequest>(
                "checkpoint",
                "Checkpoint progress.",
                validator,
                request => $"Checkpointed {request.Amount}"
            ),
            (state, request) => state with { Count = state.Count + request.Amount }
        );
        var client = new TestChatClient(
            ToolCall(
                "checkpoint-call",
                "checkpoint",
                new Dictionary<string, object?> { ["amount"] = 5 }
            )
        );
        var block = new AgentBlock<TestState>(
            new AgentBlockConfig<TestState>(
                "agent",
                "test",
                "Use normal capabilities.",
                [CreateCapability().Descriptor, checkpoint.Descriptor],
                _ => "Normal turn.",
                null,
                StructuredOutput: new AgentStructuredOutputDescriptor<TestState, TestState>(
                    (_, _) => throw new InvalidOperationException("Checkpoint turn parsed output."),
                    (state, _) => state,
                    "test",
                    null
                )
                {
                    ExampleFactory = _ =>
                        [new AgentOutputExampleDescriptor("ordinary example", "example output")],
                },
                Checkpoint: new AgentCheckpointDescriptor<TestState>(
                    100,
                    20,
                    100,
                    checkpoint.Descriptor,
                    "Write a checkpoint.",
                    (_, _) => "Checkpoint now."
                ),
                MessageAugmentations:
                [
                    (_, _) => ValueTask.FromResult<string?>("ordinary augmentation"),
                ],
                ContinueSession: true
            ),
            client
        );
        var runtime = PipelineRuntime
            .Create(Guid.CreateVersion7())
            .WithStep(
                "agent",
                step =>
                    step with
                    {
                        Usage = new AgentUsage(90, 0, 90, 100),
                        Latches = step.Latches.Add("checkpoint-required"),
                    }
            );

        var output = await block.ExecuteAsync(
            new PipelineMessage<TestState>(runtime, new TestState(0)),
            CancellationToken.None
        );

        client
            .AdvertisedTools.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(["increment", "checkpoint"]);
        client.Requests.Should().ContainSingle();
        client
            .Requests.Single()
            .Select(message => message.Text)
            .Should()
            .NotContain(text =>
                text.Contains("ordinary augmentation", StringComparison.Ordinal)
                || text.Contains("ordinary example", StringComparison.Ordinal)
                || text.Contains("example output", StringComparison.Ordinal)
            );
        output.State.Count.Should().Be(5);
        output.LatestOutcome!.Kind.Should().Be(CapabilityKind("checkpoint"));
        output.Runtime.Step("agent").Session.Should().BeNull();
        output.Runtime.Step("agent").Usage.Should().BeNull();
    }

    [Fact]
    public async Task UsageThreshold_LatchesForTheNextRequest_AndAcceptedCheckpointReleasesIt()
    {
        var checkpoint = AgentCapabilities.Create(
            new TestCapabilityDefinition<TestState, IncrementRequest>(
                "checkpoint",
                "Checkpoint progress.",
                new InlineValidator<IncrementRequest>(),
                request => $"Checkpointed {request.Amount}"
            ),
            (state, request) => state with { Count = state.Count + request.Amount }
        );
        var policy = new AgentCheckpointDescriptor<TestState>(
            100,
            20,
            80,
            checkpoint.Descriptor,
            "Write a checkpoint.",
            (_, _) => "Checkpoint now."
        );
        var client = new TestChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Still working."))
            {
                Usage = new UsageDetails { InputTokenCount = 70, OutputTokenCount = 1 },
            },
            ToolCall(
                "checkpoint-call",
                "checkpoint",
                new Dictionary<string, object?> { ["amount"] = 1 }
            )
        );
        var block = new AgentBlock<TestState>(
            new AgentBlockConfig<TestState>(
                "agent",
                "test",
                "Work.",
                [checkpoint.Descriptor],
                _ => "Work now.",
                null,
                null,
                Checkpoint: policy,
                ContinueSession: true,
                LatchedGates:
                [
                    new AgentLatchedGateDescriptor(
                        "checkpoint-required",
                        usage =>
                            usage.CurrentContextTokens + policy.MaxOutputTokens
                            >= policy.CheckpointAtTokens,
                        new HashSet<ToolEffect> { ToolEffect.WorkspaceMutation },
                        "Checkpoint required.",
                        checkpoint.Descriptor.CapabilityId,
                        checkpoint.Descriptor.ToolName,
                        true
                    ),
                ]
            ),
            client
        );
        var output = await block.ExecuteAsync(
            new PipelineMessage<TestState>(
                PipelineRuntime.Create(Guid.CreateVersion7()),
                new TestState(0)
            ),
            CancellationToken.None
        );

        client.CallCount.Should().Be(2);
        output.Runtime.IsGateLatched("agent", "checkpoint-required").Should().BeFalse();
        output.Runtime.Step("agent").Session.Should().BeNull();
        output.Runtime.Step("agent").Usage.Should().BeNull();
        output.State.Count.Should().Be(1);
    }

    [Fact]
    public async Task CheckpointRetainSessionBehavior_KeepsConversationAfterRelease()
    {
        var checkpoint = AgentCapabilities.Create(
            new TestCapabilityDefinition<TestState, IncrementRequest>(
                "checkpoint",
                "Checkpoint progress.",
                new InlineValidator<IncrementRequest>(),
                request => $"Checkpointed {request.Amount}"
            ),
            (state, request) => state with { Count = state.Count + request.Amount }
        );
        var client = new TestChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Still working."))
            {
                Usage = new UsageDetails { InputTokenCount = 70, OutputTokenCount = 1 },
            },
            ToolCall(
                "checkpoint-call",
                "checkpoint",
                new Dictionary<string, object?> { ["amount"] = 1 }
            ),
            ToolCall(
                "completion-call",
                "checkpoint",
                new Dictionary<string, object?> { ["amount"] = 1 }
            )
        );
        var agent = Agent
            .Create<TestState>("agent", "Work.", client)
            .WithMessage(_ => "Work now.")
            .ContinueSession()
            .WithCheckpoint(
                new CheckpointPolicy<TestState>(
                    100,
                    20,
                    80,
                    checkpoint,
                    "Write a checkpoint.",
                    _ => "Checkpoint now.",
                    CheckpointSessionBehavior.Retain
                )
            )
            .Build();
        var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
        var pipeline = Pipeline
            .Start(agent, "checkpoint-retain")
            .Route(agent.Success, state => state.Count == 1, agent, "continue")
            .Route(agent.Success, state => state.Count == 2, complete, "complete")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(pipeline, new TestState(0));

        result.State.Count.Should().Be(2);
        client.CallCount.Should().Be(3);
        client
            .Requests[2]
            .Should()
            .Contain(message =>
                message.Role == ChatRole.Assistant
                && message.Text.Contains("Still working.", StringComparison.Ordinal)
            );
        var retainedContents = client.Requests[2].SelectMany(message => message.Contents).ToArray();
        retainedContents
            .OfType<FunctionCallContent>()
            .Should()
            .Contain(call => call.CallId == "checkpoint-call");
        retainedContents
            .OfType<FunctionResultContent>()
            .Should()
            .Contain(result => result.CallId == "checkpoint-call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedCapabilityResult_IsRetainedNextToItsCallInTheMafSession(bool harness)
    {
        var client = new TestChatClient(
            ToolCall("first-call", "increment", new Dictionary<string, object?> { ["amount"] = 1 }),
            ToolCall("second-call", "increment", new Dictionary<string, object?> { ["amount"] = 1 })
        );

        var result = await RunRetainedCapabilityTwiceAsync(client, harness);

        result.State.Count.Should().Be(2);
        AssertEveryCallAnsweredAdjacently(client.Requests[1], "first-call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedCapability_InAParallelCallBatch_LeavesEveryCallAnsweredInTheMafSession(
        bool harness
    )
    {
        var client = new TestChatClient(
            new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "accepted-call",
                            "increment",
                            new Dictionary<string, object?> { ["amount"] = 1 }
                        ),
                        new FunctionCallContent(
                            "sibling-call",
                            "increment",
                            new Dictionary<string, object?> { ["amount"] = 1 }
                        ),
                    ]
                )
            )
            {
                FinishReason = ChatFinishReason.ToolCalls,
            },
            ToolCall("second-call", "increment", new Dictionary<string, object?> { ["amount"] = 1 })
        );

        var result = await RunRetainedCapabilityTwiceAsync(client, harness);

        result.State.Count.Should().Be(2);
        AssertEveryCallAnsweredAdjacently(client.Requests[1], "accepted-call", "sibling-call");
    }

    private static async Task<PipelineRunResult<TestState>> RunRetainedCapabilityTwiceAsync(
        TestChatClient client,
        bool harness
    )
    {
        var builder = Agent
            .Create<TestState>("agent", "Work.", client)
            .WithMessage(state => $"Visit {state.Count}.")
            .ContinueSession()
            .WithCapability(CreateCapability());
        var agent = (harness ? builder.UseHarness("Use the capability.") : builder).Build();
        var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
        var pipeline = Pipeline
            .Start(agent, "retained-capability")
            .Route(agent.Success, state => state.Count == 1, agent, "again")
            .Route(agent.Success, state => state.Count == 2, complete, "complete")
            .Build(complete);
        return await new PipelineRunner().RunAsync(pipeline, new TestState(0));
    }

    private static void AssertEveryCallAnsweredAdjacently(
        IReadOnlyList<ChatMessage> history,
        params string[] callIds
    )
    {
        var callIndex = history
            .Select((message, index) => (message, index))
            .Single(item =>
                item.message.Contents.OfType<FunctionCallContent>()
                    .Any(call => call.CallId == callIds[0])
            )
            .index;
        var toolMessages = history
            .Skip(callIndex + 1)
            .TakeWhile(message => message.Role == ChatRole.Tool)
            .ToArray();
        var results = toolMessages
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .ToArray();
        results.Select(result => result.CallId).Should().BeEquivalentTo(callIds);
        results
            .Single(result => result.CallId == callIds[0])
            .Result?.ToString()
            .Should()
            .Contain("\"accepted\":true");
        history[callIndex + 1 + toolMessages.Length].Text.Should().Be("Visit 1.");
    }

    [Fact]
    public void CheckpointSessionBehavior_RejectsUnknownValue()
    {
        var checkpoint = CreateCapability();
        var builder = Agent.Create<TestState>("agent", "Work.", new TestChatClient());

        var act = () =>
            builder.WithCheckpoint(
                new CheckpointPolicy<TestState>(
                    100,
                    20,
                    80,
                    checkpoint,
                    "Write a checkpoint.",
                    _ => "Checkpoint now.",
                    (CheckpointSessionBehavior)42
                )
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, 20, 80)]
    [InlineData(100, 0, 80)]
    [InlineData(100, 100, 80)]
    [InlineData(100, 20, 0)]
    [InlineData(100, 20, 100)]
    public void CheckpointPolicy_RejectsInvalidLimits(
        int contextWindowTokens,
        int maxOutputTokens,
        int checkpointAtPercent
    )
    {
        var builder = Agent.Create<TestState>("agent", "Work.", new TestChatClient());

        var act = () =>
            builder.WithCheckpoint(
                new CheckpointPolicy<TestState>(
                    contextWindowTokens,
                    maxOutputTokens,
                    checkpointAtPercent,
                    CreateCapability(),
                    "Write a checkpoint.",
                    _ => "Checkpoint now."
                )
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static string CapabilityKind(string name) =>
        $"capability:{typeof(TestState).FullName}:{name}";

    private static AIFunctionArguments Arguments(int amount) => new() { ["amount"] = amount };

    private static bool IsError(object? result) => result is Tandem.Infrastructure.ToolError;

    private static AgentCapability<TestState, IncrementRequest> CreateCapability(
        Action<IncrementRequest>? accepted = null
    )
    {
        var validator = new InlineValidator<IncrementRequest>();
        validator.RuleFor(request => request.Amount).GreaterThan(0);
        return AgentCapabilities.Create(
            new TestCapabilityDefinition<TestState, IncrementRequest>(
                "increment",
                "Increment the state.",
                validator,
                request => $"Incremented by {request.Amount}"
            ),
            (state, request) =>
            {
                accepted?.Invoke(request);
                return state with { Count = state.Count + request.Amount };
            }
        );
    }

    private static AgentBlock<TestState> CreateBlock(
        IChatClient client,
        AgentCapability<TestState> capability
    ) =>
        new(
            new AgentBlockConfig<TestState>(
                "agent",
                "test",
                "Call the increment capability.",
                [capability.Descriptor],
                _ => "Increment the state.",
                null,
                null,
                ContinueSession: true
            ),
            client
        );

    private static async Task<PipelineMessage<TestState>> RunBlockAsync(
        AgentBlock<TestState> block
    ) => await RunBlockAsync(block, new TestState(0));

    private static async Task<PipelineMessage<TState>> RunBlockAsync<TState>(
        AgentBlock<TState> block,
        TState initialState
    )
    {
        var binding = block.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();
        var input = new PipelineMessage<TState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            initialState
        );
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            input,
            input.Runtime.RunId.ToString("N"),
            CancellationToken.None
        );

        PipelineMessage<TState>? output = null;
        Exception? failure = null;
        await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowErrorEvent error)
            {
                failure = error.Exception;
            }
            else if (evt is ExecutorFailedEvent failed)
            {
                failure = failed.Data;
            }
            else if (
                evt is WorkflowOutputEvent completed
                && completed.Is<PipelineMessage<TState>>()
            )
            {
                output = completed.As<PipelineMessage<TState>>();
            }
        }

        failure.Should().BeNull();
        return output
            ?? throw new InvalidOperationException("Capability block produced no output.");
    }

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

    private sealed record TestState(int Count);

    private sealed record IncrementRequest(int Amount);

    private sealed class OrderedCapabilityDefinition(List<string> order)
        : IAgentCapabilityDefinition<TestState, IncrementRequest>
    {
        public string ToolName => "increment";
        public string Instructions => "Increment the state.";
        public IValidator<IncrementRequest> Validator { get; } =
            CreateValidator("intrinsic", order);

        public IValidator<IncrementRequest> ValidatorFor(TestState state) =>
            CreateValidator("contextual", order, fail: true);

        public string Summarize(IncrementRequest request) => $"Incremented by {request.Amount}";

        private static IValidator<IncrementRequest> CreateValidator(
            string name,
            List<string> order,
            bool fail = false
        )
        {
            var validator = new InlineValidator<IncrementRequest>();
            validator
                .RuleFor(request => request.Amount)
                .Custom(
                    (_, context) =>
                    {
                        order.Add(name);
                        if (fail)
                        {
                            context.AddFailure("Context rejected the amount.");
                        }
                    }
                );
            return validator;
        }
    }

    private sealed class FailingAcceptanceObserver : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        ) =>
            observation is PipelineCapabilityAccepted
                ? ValueTask.FromException(new IOException("Journal failed."))
                : ValueTask.CompletedTask;
    }
}
