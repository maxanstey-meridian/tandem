using System.Runtime.CompilerServices;
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
            _ => []
        );
        var outputCall = () =>
            Agent
                .Create<JsonState>("agent", "Decide.", new ScriptedChatClient())
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
                    _ => "set"
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
            (state, candidate) =>
            {
                order.Add("contextual");
                return candidate.GetProperty("value").GetInt32() > state.Maximum
                    ? [new AgentJsonValidationProblem("$.value", "Exceeds maximum.")]
                    : [];
            },
            "example.dynamic-value"
        );
        var client = new ScriptedChatClient(Response("{\"value\":0}"), Response("{\"value\":3}"));
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
        order.Should().Equal("intrinsic", "contextual", "intrinsic", "contextual", "map");
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
        var client = new ScriptedChatClient(Response("not json"), Response("[]"));
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

    [Fact]
    public async Task JsonOutput_ValidationCallbackFailureIsCorrectable_ButCancellationPropagates()
    {
        var attempts = 0;
        var client = new ScriptedChatClient(Response("{\"value\":1}"), Response("{\"value\":2}"));
        var agent = Agent
            .Create<JsonState>("agent", "Decide.", client)
            .WithMessage(_ => "Return a value.")
            .WithJsonOutput(
                JsonOutput(_ =>
                    ++attempts == 1
                        ? throw new InvalidOperationException("Validator unavailable.")
                        : []
                ),
                (state, output) => state with { Value = output.GetProperty("value").GetInt32() }
            )
            .Build();

        var corrected = await new PipelineRunner().RunAsync(
            Pipeline.Start(agent, "callback-correction").Build(agent),
            new JsonState(0, 5)
        );

        corrected.State.Value.Should().Be(2);
        client.CallCount.Should().Be(2);

        var cancelledAgent = Agent
            .Create<JsonState>(
                "cancelled",
                "Decide.",
                new ScriptedChatClient(Response("{\"value\":1}"))
            )
            .WithMessage(_ => "Return a value.")
            .WithJsonOutput(
                JsonOutput(_ => throw new OperationCanceledException("Validation cancelled.")),
                (state, _) => state
            )
            .Build();
        var run = async () =>
            await new PipelineRunner().RunAsync(
                Pipeline.Start(cancelledAgent, "callback-cancellation").Build(cancelledAgent),
                new JsonState(0, 5)
            );

        var exception = await run.Should().ThrowAsync<PipelineRunException>();
        exception.Which.InnerException.Should().BeOfType<OperationCanceledException>();
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

        order.Should().Equal("intrinsic", "contextual", "intrinsic", "contextual");
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
    public async Task JsonCapability_CallbackFailuresAreErrors_CancellationPropagates_AndCallsConflict()
    {
        var callbackFailure = JsonCapability(
            _ => throw new InvalidOperationException("Validator unavailable."),
            _ => "unused"
        );
        var failed = (JsonElement)
            (await callbackFailure.Bind(Invocation()).InvokeAsync(Arguments(1)))!;
        failed.GetProperty("isError").GetBoolean().Should().BeTrue();
        failed.GetProperty("problems")[0].GetString().Should().Contain("Validator unavailable");

        var cancellation = JsonCapability(
            _ => throw new OperationCanceledException("Validation cancelled."),
            _ => "unused"
        );
        var cancel = async () => await cancellation.Bind(Invocation()).InvokeAsync(Arguments(1));
        await cancel.Should().ThrowAsync<OperationCanceledException>();

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

    private static AgentJsonOutputDefinition<JsonState> JsonOutput(
        Func<JsonElement, IReadOnlyList<AgentJsonValidationProblem>> validate
    ) =>
        new(
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
            "Return JSON.",
            validate
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
        Func<JsonElement, string> summarize
    ) =>
        new(
            "set_value",
            "Set the value.",
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
            validate,
            null,
            summarize
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

    private sealed class RecordingObserver(List<PipelineObservation> observations)
        : IPipelinePersistenceObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            observations.Add(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

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
}
