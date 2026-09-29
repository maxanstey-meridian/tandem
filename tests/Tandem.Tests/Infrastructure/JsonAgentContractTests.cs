using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class JsonAgentContractTests
{
    [Fact]
    public void JsonContracts_RequireDeclaredObjectRootsAndAuthoritativeValidators()
    {
        using var undeclaredRoot = JsonDocument.Parse("{}");
        var output = new AgentJsonOutputDefinition<JsonState>(
            undeclaredRoot.RootElement,
            "Return JSON.",
            _ => [],
            "test.value"
        );
        var outputCall = () =>
            Agent
                .Create<JsonState>("agent", "Decide.", new TestChatClient())
                .WithJsonOutput(output, (state, _) => state);
        outputCall.Should().Throw<ArgumentException>().WithMessage("*type 'object'*");

        var capabilityCall = () =>
            AgentCapabilities.CreateJson(
                new AgentJsonCapabilityDefinition<JsonState>(
                    "set_value",
                    "Set value.",
                    undeclaredRoot.RootElement,
                    null!,
                    null,
                    _ => "set",
                    "test.value"
                ),
                (state, _) => state
            );
        capabilityCall.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task JsonOutput_CorrectsValidationInOrder_AndPersistsSemanticValue()
    {
        var order = new List<string>();
        var observations = new List<PipelineObservation>();
        using var schemaDocument = JsonDocument.Parse("{\"type\":\"object\"}");
        var definition = new AgentJsonOutputDefinition<JsonState>(
            schemaDocument.RootElement,
            "Return a value.",
            candidate =>
            {
                order.Add("intrinsic");
                return candidate.GetProperty("value").GetInt32() > 0
                    ? []
                    : [new AgentJsonValidationProblem("$.value", "Must be positive.")];
            },
            "example.dynamic-value",
            (state, candidate) =>
            {
                order.Add("contextual");
                return candidate.GetProperty("value").GetInt32() > state.Maximum
                    ? [new AgentJsonValidationProblem("$.value", "Exceeds maximum.")]
                    : [];
            }
        );
        var client = new TestChatClient(Response("{\"value\":0}"), Response("{\"value\":3}"));
        var mappings = 0;
        var agent = Agent
            .Create<JsonState>("agent", "Decide.", client)
            .WithMessage(_ => "Return a value.")
            .WithJsonOutput(
                definition,
                (state, output) =>
                {
                    order.Add("map");
                    mappings++;
                    return state with { Value = output.GetProperty("value").GetInt32() };
                }
            )
            .Build();
        schemaDocument.Dispose();
        var pipeline = Pipeline.Start(agent, "json-output").Persist().Build(agent);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new JsonState(0, 5),
            new PipelineRunOptions(Observer: new RecordingObserver(observations))
        );

        result.Succeeded.Should().BeTrue();
        result.State.Value.Should().Be(3);
        mappings.Should().Be(1);
        order.Should().Equal("intrinsic", "intrinsic", "contextual", "map");
        client.CallCount.Should().Be(2);
        client
            .Requests[1]
            .Select(message => message.Text)
            .Should()
            .Contain(text => text.Contains("$.value"));
        var accepted = observations
            .OfType<PipelineStructuredOutputAccepted>()
            .Should()
            .ContainSingle()
            .Which;
        accepted.OutputType.Should().Be("example.dynamic-value");
        accepted.Payload!.Value.GetProperty("value").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task JsonOutput_MalformedThenInvalid_FailsClosedWithoutMapping()
    {
        var client = new TestChatClient(Response("not json"), Response("[]"));
        var mappings = 0;
        var agent = Agent
            .Create<JsonState>("agent", "Decide.", client)
            .WithMessage(_ => "Return a value.")
            .WithJsonOutput(
                JsonOutput(_ => []),
                (state, _) =>
                {
                    mappings++;
                    return state;
                }
            )
            .Build();

        var result = await new PipelineRunner().RunAsync(
            Pipeline.Start(agent, "invalid-json-output").Build(agent),
            new JsonState(0, 5)
        );

        result.Status.Should().Be(PipelineRunStatus.Failed);
        mappings.Should().Be(0);
        client.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(JsonContract.Output)]
    [InlineData(JsonContract.Capability)]
    public async Task JsonContract_ValidatorFailuresAndCancellationPropagate(JsonContract contract)
    {
        (
            await FaultAsync(
                contract,
                validate: _ => throw new InvalidOperationException("Validator unavailable.")
            )
        )
            .Should()
            .BeOfType<InvalidOperationException>();
        (
            await FaultAsync(
                contract,
                validate: _ => throw new OperationCanceledException("Validation cancelled.")
            )
        )
            .Should()
            .BeOfType<OperationCanceledException>();
    }

    [Theory]
    [InlineData(JsonContract.Output)]
    [InlineData(JsonContract.Capability)]
    public async Task JsonContract_ContextualValidationAndApplyFailuresPropagate(
        JsonContract contract
    )
    {
        (
            await FaultAsync(
                contract,
                validateFor: (_, _) => throw new InvalidOperationException("Context unavailable.")
            )
        )
            .Should()
            .BeOfType<InvalidOperationException>();
        (
            await FaultAsync(
                contract,
                apply: (_, _) => throw new InvalidOperationException("Apply failed.")
            )
        )
            .Should()
            .BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task JsonCapability_PreservesSchemaAndValidationPaths_ThenAcceptsCorrectedPayload()
    {
        using var schemaDocument = JsonDocument.Parse("{\"type\":\"object\"}");
        var order = new List<string>();
        var definition = new AgentJsonCapabilityDefinition<JsonState>(
            "set_value",
            "Set the value.",
            schemaDocument.RootElement,
            request =>
            {
                order.Add("intrinsic");
                return request.GetProperty("value").GetInt32() > 0
                    ? []
                    : [new AgentJsonValidationProblem("$.value", "Must be positive.")];
            },
            (state, request) =>
            {
                order.Add("contextual");
                return request.GetProperty("value").GetInt32() <= state.Maximum
                    ? []
                    : [new AgentJsonValidationProblem("$.value", "Exceeds maximum.")];
            },
            request => $"Set {request.GetProperty("value").GetInt32()}",
            "example.capability-value"
        );
        var capability = AgentCapabilities.CreateJson(
            definition,
            (state, request) => state with { Value = request.GetProperty("value").GetInt32() }
        );
        schemaDocument.Dispose();
        var observations = new List<PipelineObservation>();
        var runId = Guid.CreateVersion7();
        var invocation = new CapabilityInvocationState<JsonState>(
            runId,
            "agent",
            "invocation",
            new JsonState(0, 5),
            new PipelineRunContext(
                runId,
                new RecordingObserver(observations),
                persistentStepIds: new HashSet<string>(StringComparer.Ordinal) { "agent" }
            )
        );
        var function = capability.Bind(invocation);

        function.JsonSchema.GetProperty("type").GetString().Should().Be("object");
        var invalid = (JsonElement)(await function.InvokeAsync(Arguments(0)))!;
        invalid.GetProperty("problems")[0].GetProperty("field").GetString().Should().Be("$.value");
        invocation.Accepted.Should().BeNull();
        await function.InvokeAsync(Arguments(3));

        order.Should().Equal("intrinsic", "intrinsic", "contextual");
        invocation.Accepted!.State.Value.Should().Be(3);
        var accepted = observations
            .OfType<PipelineCapabilityAccepted>()
            .Should()
            .ContainSingle()
            .Which;
        accepted.RequestType.Should().Be("example.capability-value");
        accepted.Payload!.Value.GetProperty("value").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task JsonCapability_ConcurrentCallsConflict()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var mappings = 0;
        var capability = AgentCapabilities.CreateJson(
            JsonCapabilityDefinition(_ => [], _ => "accepted"),
            (state, request) =>
            {
                Interlocked.Increment(ref mappings);
                entered.Set();
                release.Wait();
                return state with { Value = request.GetProperty("value").GetInt32() };
            }
        );
        var invocation = Invocation();
        var function = capability.Bind(invocation);
        var first = Task.Run(async () => await function.InvokeAsync(Arguments(1)));
        entered.Wait();
        var second = (JsonElement)(await function.InvokeAsync(Arguments(2)))!;
        release.Set();
        await first;

        mappings.Should().Be(1);
        second.GetProperty("error").GetString().Should().Be("conflicting capability outcome");
        invocation.Accepted!.State.Value.Should().Be(1);
    }

    [Fact]
    public async Task JsonCapability_SummaryFailurePropagatesAndApplyFailureAllowsRetry()
    {
        var summary = JsonCapability(
            _ => [],
            _ => throw new InvalidOperationException("Summary unavailable.")
        );
        var summaryCall = async () => await summary.Bind(Invocation()).InvokeAsync(Arguments(1));
        await summaryCall.Should().ThrowAsync<InvalidOperationException>();

        var attempts = 0;
        var invocation = Invocation();
        var apply = AgentCapabilities.CreateJson(
            JsonCapabilityDefinition(_ => [], _ => "accepted"),
            (state, request) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("Apply failed.");
                }
                return state with { Value = request.GetProperty("value").GetInt32() };
            }
        );
        var function = apply.Bind(invocation);
        var applyCall = async () => await function.InvokeAsync(Arguments(1));

        await applyCall.Should().ThrowAsync<InvalidOperationException>();
        invocation.Accepted.Should().BeNull();
        await function.InvokeAsync(Arguments(4));
        invocation.Accepted!.State.Value.Should().Be(4);
    }

    [Fact]
    public async Task JsonCapability_AdvancedAcceptanceReceivesSharedInvocationProjection()
    {
        IReadOnlyList<ToolInvocationObservation>? observations = null;
        var invocation = Invocation();
        var collector = new Tandem.Infrastructure.Blocks.ToolOutcomeCollector();
        invocation.AttachToolOutcomeCollector(collector);
        AddInvocation(
            collector,
            new Tandem.Infrastructure.ToolInvocationObservationDescriptor(
                "failed",
                new Tandem.Infrastructure.ToolSemantics(
                    Tandem.Infrastructure.ToolEffect.ProcessExecution
                ),
                JsonDocument.Parse("{\"value\":1}").RootElement.Clone(),
                Tandem.Infrastructure.ToolInvocationStatus.Failed,
                new Tandem.Infrastructure.ToolResultEvidenceDescriptor.Process(
                    7,
                    "stdout",
                    "stderr",
                    TimeSpan.FromSeconds(2),
                    true,
                    true
                )
            )
        );
        AddInvocation(
            collector,
            new Tandem.Infrastructure.ToolInvocationObservationDescriptor(
                "blocked",
                null,
                JsonDocument.Parse("{}").RootElement.Clone(),
                Tandem.Infrastructure.ToolInvocationStatus.Blocked,
                null
            )
        );
        AddInvocation(
            collector,
            new Tandem.Infrastructure.ToolInvocationObservationDescriptor(
                "faulted",
                null,
                JsonDocument.Parse("{}").RootElement.Clone(),
                Tandem.Infrastructure.ToolInvocationStatus.Faulted,
                null
            )
        );
        var capability = AgentCapabilities
            .CreateJson(JsonCapabilityDefinition(_ => [], _ => "accepted"), (state, _) => state)
            .WithAcceptance(
                (context, _) =>
                {
                    observations = context.ToolInvocations;
                    return ValueTask.CompletedTask;
                }
            );

        await capability.Bind(invocation).InvokeAsync(Arguments(1));

        observations.Should().NotBeNull();
        observations!
            .Select(item => item.Status)
            .Should()
            .Equal(
                ToolInvocationStatus.Failed,
                ToolInvocationStatus.Blocked,
                ToolInvocationStatus.Faulted
            );
        var process = observations[0]
            .Result.Should()
            .BeOfType<ToolResultEvidence.Process>()
            .Subject;
        process
            .Should()
            .Be(
                new ToolResultEvidence.Process(
                    7,
                    "stdout",
                    "stderr",
                    TimeSpan.FromSeconds(2),
                    true,
                    true
                )
            );
        observations[0].Arguments.GetProperty("value").GetInt32().Should().Be(1);
        observations[1].Result.Should().BeNull();
        observations[2].Result.Should().BeNull();
    }

    private static void AddInvocation(
        Tandem.Infrastructure.Blocks.ToolOutcomeCollector collector,
        Tandem.Infrastructure.ToolInvocationObservationDescriptor observation
    )
    {
        var reservation = collector.ReserveToolInvocation();
        collector.CompleteToolInvocation(reservation, observation);
    }

    [Fact]
    public async Task JsonCapability_AdvancedAcceptanceUsesJsonRequestAndObserverFailureAllowsRetry()
    {
        JsonElement acceptedRequest = default;
        var observerAttempts = 0;
        var runId = Guid.CreateVersion7();
        var invocation = new CapabilityInvocationState<JsonState>(
            runId,
            "agent",
            "invocation",
            new JsonState(0, 5),
            new PipelineRunContext(
                runId,
                new DelegatingObserver(
                    (_, _) =>
                    {
                        if (Interlocked.Increment(ref observerAttempts) == 1)
                        {
                            throw new IOException("Observer failed.");
                        }
                        return ValueTask.CompletedTask;
                    }
                )
            )
        );
        var capability = AgentCapabilities
            .CreateJson(
                JsonCapabilityDefinition(_ => [], _ => "accepted"),
                (state, request) => state with { Value = request.GetProperty("value").GetInt32() }
            )
            .WithAcceptance(
                (context, _) =>
                {
                    acceptedRequest = context.Request;
                    return ValueTask.CompletedTask;
                }
            );
        var function = capability.Bind(invocation);

        var failed = (JsonElement)(await function.InvokeAsync(Arguments(1)))!;
        failed.GetProperty("isError").GetBoolean().Should().BeTrue();
        invocation.Accepted.Should().BeNull();
        await function.InvokeAsync(Arguments(3));

        acceptedRequest.GetProperty("value").GetInt32().Should().Be(3);
        invocation.Accepted!.State.Value.Should().Be(3);
    }

    public enum JsonContract
    {
        Output,
        Capability,
    }

    private static async Task<Exception> FaultAsync(
        JsonContract contract,
        Func<JsonElement, IReadOnlyList<AgentJsonValidationProblem>>? validate = null,
        Func<JsonState, JsonElement, IReadOnlyList<AgentJsonValidationProblem>>? validateFor = null,
        Func<JsonState, JsonElement, JsonState>? apply = null
    )
    {
        validate ??= _ => [];
        apply ??= (state, _) => state;
        if (contract == JsonContract.Capability)
        {
            var capability = AgentCapabilities.CreateJson(
                JsonCapabilityDefinition(validate, _ => "accepted", validateFor),
                apply
            );
            var invoke = async () => await capability.Bind(Invocation()).InvokeAsync(Arguments(1));
            return (await invoke.Should().ThrowAsync<Exception>()).Which;
        }
        var client = new TestChatClient(Response("{\"value\":1}"));
        var output = new AgentJsonOutputDefinition<JsonState>(
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
            "Return JSON.",
            validate,
            "test.dynamic-value",
            validateFor
        );
        var agent = Agent
            .Create<JsonState>("agent", "Decide.", client)
            .WithMessage(_ => "Return a value.")
            .WithJsonOutput(output, apply)
            .Build();
        var run = async () =>
            await new PipelineRunner().RunAsync(
                Pipeline.Start(agent, "json-output-fault").Build(agent),
                new JsonState(0, 5)
            );
        var failure = await run.Should().ThrowAsync<PipelineRunException>();
        client.CallCount.Should().Be(1);
        return failure.Which.InnerException!;
    }

    private static AgentJsonOutputDefinition<JsonState> JsonOutput(
        Func<JsonElement, IReadOnlyList<AgentJsonValidationProblem>> validate
    ) =>
        new(
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
            "Return JSON.",
            validate,
            "test.dynamic-value"
        );

    private static AgentCapability<JsonState> JsonCapability(
        Func<JsonElement, IReadOnlyList<AgentJsonValidationProblem>> validate,
        Func<JsonElement, string> summarize
    ) =>
        AgentCapabilities.CreateJson(
            JsonCapabilityDefinition(validate, summarize),
            (state, _) => state
        );

    private static AgentJsonCapabilityDefinition<JsonState> JsonCapabilityDefinition(
        Func<JsonElement, IReadOnlyList<AgentJsonValidationProblem>> validate,
        Func<JsonElement, string> summarize,
        Func<JsonState, JsonElement, IReadOnlyList<AgentJsonValidationProblem>>? validateFor = null
    ) =>
        new(
            "set_value",
            "Set the value.",
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
            validate,
            validateFor,
            summarize,
            "test.dynamic-value"
        );

    private static CapabilityInvocationState<JsonState> Invocation() =>
        new(Guid.CreateVersion7(), "agent", "invocation", new JsonState(0, 5));

    private static AIFunctionArguments Arguments(int value) => new() { ["value"] = value };

    private static ChatResponse Response(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    private sealed record JsonState(int Value, int Maximum);

    private sealed class DelegatingObserver(
        Func<PipelineObservation, CancellationToken, ValueTask> observe
    ) : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        ) => observe(observation, cancellationToken);
    }
}
