using System.Runtime.CompilerServices;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class TypedStructuredOutputAcceptanceTests
{
    [Fact]
    public void TypedContract_ReturnsIndependentOptions()
    {
        var first = TandemJson.CreateTypedContract();
        var second = TandemJson.CreateTypedContract();

        first.PropertyNamingPolicy = null;

        second.PropertyNamingPolicy.Should().Be(System.Text.Json.JsonNamingPolicy.CamelCase);
        TandemJson
            .CreateTypedContract()
            .PropertyNamingPolicy.Should()
            .Be(System.Text.Json.JsonNamingPolicy.CamelCase);
    }

    [Fact]
    public async Task TypedStructuredOutput_PreservesAcceptedValueForInProcessObservers_AndProjectsCanonicalJson()
    {
        var observations = new List<PipelineObservation>();
        var client = new ScriptedChatClient(Response("""{"decision":"Proceed","summary":"go"}"""));
        var agent = Agent
            .Create<DecisionState>("planner", "Decide.", client)
            .WithMessage(_ => "Decide.")
            .WithOutput(
                new DecisionOutputDefinition(),
                (state, decision) => state with { Decision = decision.Decision }
            )
            .Build();
        var complete = PipelineNodes.Complete(new TestCompletion<DecisionState>("done"));
        var pipeline = Pipeline
            .Start(agent, "typed-acceptance")
            .Persist()
            .Route(agent.Success, complete, "complete")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new DecisionState(null),
            new PipelineRunOptions(Observer: new RecordingObserver(observations))
        );

        result.Succeeded.Should().BeTrue();
        result.State.Decision.Should().Be(DecisionValue.Proceed);
        var accepted = observations
            .OfType<OutputAccepted<PlannerDecision>>()
            .Should()
            .ContainSingle()
            .Which;

        accepted.OutputType.Should().Be(typeof(PlannerDecision).FullName);
        accepted.Payload.Should().NotBeNull();
        accepted.Payload!.Value.GetProperty("decision").GetString().Should().Be("Proceed");

        accepted.AcceptedValue.Decision.Should().Be(DecisionValue.Proceed);
        accepted.AcceptedValue.Summary.Should().Be("go");
    }

    [Fact]
    public async Task TypedStructuredOutput_CorrectsInvalidOutputThenRecordsTypedValueWithoutReserialization()
    {
        var sink = new TypedDecisionSink();
        var client = new ScriptedChatClient(
            Response("""{"decision":"Proceed","summary":""}"""),
            Response("""{"decision":"Proceed","summary":"go"}""")
        );
        var agent = Agent
            .Create<DecisionState>("planner", "Decide.", client)
            .WithMessage(_ => "Decide.")
            .WithOutput(
                new DecisionOutputDefinition(),
                (state, decision) => state with { Decision = decision.Decision }
            )
            .Build();
        var complete = PipelineNodes.Complete(new TestCompletion<DecisionState>("done"));
        var pipeline = Pipeline
            .Start(agent, "typed-correction")
            .Persist()
            .Route(agent.Success, complete, "complete")
            .Build(complete);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new DecisionState(null),
            new PipelineRunOptions(Observer: sink)
        );

        result.Succeeded.Should().BeTrue();
        result.State.Decision.Should().Be(DecisionValue.Proceed);
        client.CallCount.Should().Be(2);
        client
            .Requests[1]
            .Select(message => message.Text)
            .Should()
            .Contain(text => text.Contains("summary"));
        sink.Recorded.Should().NotBeNull();
        sink.Recorded!.Decision.Should().Be(DecisionValue.Proceed);
        sink.Recorded.Summary.Should().Be("go");
    }

    private sealed record DecisionState(DecisionValue? Decision);

    private enum DecisionValue
    {
        Proceed,
        Stop,
    }

    private sealed record PlannerDecision(DecisionValue Decision, string Summary);

    private sealed class DecisionOutputDefinition
        : IAgentOutputDefinition<DecisionState, PlannerDecision>
    {
        public string Instructions => "Return a decision.";

        public IValidator<PlannerDecision> Validator { get; } = CreateValidator();

        private static IValidator<PlannerDecision> CreateValidator()
        {
            var validator = new InlineValidator<PlannerDecision>();
            validator.RuleFor(decision => decision.Summary).NotEmpty();
            return validator;
        }
    }

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

    private sealed class TypedDecisionSink : IPipelinePersistenceObserver
    {
        public PlannerDecision? Recorded { get; private set; }

        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            if (observation is OutputAccepted<PlannerDecision> { AcceptedValue: var decision })
            {
                Recorded = decision;
            }
            return ValueTask.CompletedTask;
        }
    }

    private static ChatResponse Response(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

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
