using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Ledger;

namespace Tandem.ExternalConsumer.Tests;

public sealed class PublicRuntimeTests
{
    [Fact]
    public async Task ExternalHost_CanPersistAndReadReturnedStateWithPublicLedgerApis()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tandem-external-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var runId = Guid.CreateVersion7();
            var stage = new DurablePublicStage();
            var pipeline = Pipeline.Start(stage, "external-durable").Persist().Build(stage);

            await new PipelineRunner().RunAsync(
                pipeline,
                new DurablePublicState(3),
                new SqlitePipelineRunOptions(path, runId)
            );

            var reopened = new SqliteLedgerStore(path);
            (await reopened.GetRunAsync(runId)).Status.Should().Be(LedgerRunStatus.Ready);
            var accepted = await reopened.ReadLatestAcceptedAsync<DurablePublicState>(
                runId,
                stage.Id
            );
            accepted.Should().NotBeNull();
            accepted!.StepId.Should().Be(stage.Id);
            accepted.Value.Count.Should().Be(4);
            accepted.Sequence.Should().BePositive();
        }
        finally
        {
            File.Delete(path);
            File.Delete($"{path}-shm");
            File.Delete($"{path}-wal");
        }
    }

    [Fact]
    public async Task Interaction_CanBeTheSemanticPipelineStart()
    {
        var interaction = PipelineNodes.WaitFor<PublicState, PublicQuestion, PublicAnswer>(
            "starting-input",
            _ => new PublicQuestion("answer"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new PublicCompletion("complete"));
        var pipeline = Pipeline
            .Start(interaction, "interaction-start")
            .Route(interaction, complete, "answered")
            .Build(complete);
        var handlers = new PipelineInteractionHandlers().Handle(
            interaction,
            (_, _) => ValueTask.FromResult(new PublicAnswer("received"))
        );

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new PublicState(0, new NonSerializableReference()),
            new PipelineRunOptions(Interactions: handlers)
        );

        result.Succeeded.Should().BeTrue();
        result.Status.Should().Be(PipelineRunStatus.Succeeded);
        result.State.Answer.Should().Be("received");
        pipeline.Inspect().StartStepId.Should().Be("starting-input");
    }

    [Fact]
    public async Task SameTypedInteractions_DispatchBySemanticIdentity()
    {
        var start = new PublicStartStage();
        var first = PipelineNodes.WaitFor<PublicState, PublicQuestion, PublicAnswer>(
            "first-input",
            _ => new PublicQuestion("first"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var second = PipelineNodes.WaitFor<PublicState, PublicQuestion, PublicAnswer>(
            "second-input",
            _ => new PublicQuestion("second"),
            (state, answer) => state with { Answer = $"{state.Answer},{answer.Text}" }
        );
        var between = new PublicBetweenStage();
        var complete = PipelineNodes.Complete(new PublicCompletion("complete"));
        var pipeline = Pipeline
            .Start(start, "identity-bound-interactions")
            .Route(start.Success, first, "first")
            .Route(_ => true, first, between, "between")
            .Route(between.Success, second, "second")
            .Route(_ => true, second, complete, "complete")
            .Build(complete);
        var handlers = new PipelineInteractionHandlers()
            .Handle(first, (_, _) => ValueTask.FromResult(new PublicAnswer("one")))
            .Handle(second, (_, _) => ValueTask.FromResult(new PublicAnswer("two")));

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new PublicState(0, new NonSerializableReference()),
            new PipelineRunOptions(Interactions: handlers)
        );

        result.State.Answer.Should().Be("one,two");
        var duplicate = () =>
            handlers.Handle(first, (_, _) => ValueTask.FromResult(new PublicAnswer("duplicate")));
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*first-input*");
    }

    [Fact]
    public async Task ExternalConsumer_CanBuildAndRunTypedPipeline()
    {
        var increment = new PublicIncrementStage();
        var pipeline = Pipeline.Start(increment, "external-straight-line").Build(increment);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new PublicState(1, new NonSerializableReference()),
            cancellationToken: CancellationToken.None
        );

        result.State.Count.Should().Be(2);
        result.Succeeded.Should().BeTrue();
        result.RunId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExternalConsumer_CanHandleTypedInteractionWithoutSerializingState()
    {
        var start = new PublicStartStage();
        var interaction = PipelineNodes.WaitFor<PublicState, PublicQuestion, PublicAnswer>(
            "public-input",
            state => new PublicQuestion($"Count: {state.Count}"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new PublicCompletion("complete"));
        var pipeline = Pipeline
            .Start(start, "external-interaction")
            .Route(start.Success, interaction, "ask")
            .Route(_ => true, interaction, complete, "answered")
            .Build(complete);
        var reference = new NonSerializableReference();
        PipelineInteractionContext<PublicQuestion, PublicAnswer>? observed = null;
        var interactions = new PipelineInteractionHandlers().Handle(
            interaction,
            (context, _) =>
            {
                observed = context;
                return ValueTask.FromResult(new PublicAnswer("continue"));
            }
        );
        var runId = Guid.CreateVersion7();
        var observer = new RecordingObserver();

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new PublicState(3, reference),
            new PipelineRunOptions(runId, interactions, observer),
            CancellationToken.None
        );

        result.State.Answer.Should().Be("continue");
        result.State.Reference.Should().BeSameAs(reference);
        observed.Should().NotBeNull();
        observed!.RunId.Should().Be(runId);
        observed.RequestId.Should().NotBeNullOrWhiteSpace();
        observed.InteractionId.Should().Be("public-input");
        observed.Request.Should().Be(new PublicQuestion("Count: 3"));
        pipeline
            .Inspect()
            .Interactions.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new PipelineInteractionInspection(
                    "public-input",
                    typeof(PublicQuestion).FullName!,
                    typeof(PublicAnswer).FullName!
                )
            );
        observer
            .Observations.OfType<PipelineInteractionRequested<PublicQuestion>>()
            .Should()
            .ContainSingle()
            .Which.RequestId.Should()
            .Be(observed.RequestId);
        observer
            .Observations.OfType<PipelineInteractionAnswered<PublicAnswer>>()
            .Should()
            .ContainSingle()
            .Which.RequestId.Should()
            .Be(observed.RequestId);
        observer
            .Observations.OfType<PipelineStepStarted>()
            .Count(observation => observation.StepId == "public-input")
            .Should()
            .Be(1);
        observer
            .Observations.OfType<PipelineStepCompleted>()
            .Count(observation => observation.StepId == "public-input")
            .Should()
            .Be(1);
        observer.Observations.Should().NotContain(observation => observation.StepId.Contains("--"));
    }

    [Fact]
    public async Task AgentExecution_PreservesObservationForDownstreamInteraction()
    {
        var agent = Agent
            .Create<PublicState>("agent", "Respond.", new TextChatClient("ready"))
            .WithMessage(state => $"Count: {state.Count}")
            .Build();
        var interaction = PipelineNodes.WaitFor<PublicState, PublicQuestion, PublicAnswer>(
            "after-agent",
            state => new PublicQuestion($"Count: {state.Count}"),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new PublicCompletion("complete"));
        var pipeline = Pipeline
            .Start(agent, "agent-observation")
            .Route(agent.Success, interaction, "ask")
            .Route(_ => true, interaction, complete, "answered")
            .Build(complete);
        var observer = new RecordingObserver();
        var interactions = new PipelineInteractionHandlers().Handle(
            interaction,
            (_, _) => ValueTask.FromResult(new PublicAnswer("continue"))
        );

        await new PipelineRunner().RunAsync(
            pipeline,
            new PublicState(1, new NonSerializableReference()),
            new PipelineRunOptions(Observer: observer, Interactions: interactions),
            CancellationToken.None
        );

        observer
            .Observations.OfType<PipelineInteractionRequested<PublicQuestion>>()
            .Should()
            .ContainSingle();
        observer
            .Observations.OfType<PipelineStepCompleted>()
            .Should()
            .Contain(observation => observation.StepId == "complete");
    }

    [Fact]
    public async Task TypedInteraction_DoesNotSerializeAuthoredRequest()
    {
        var start = new PublicStartStage();
        var interaction = PipelineNodes.WaitFor<PublicState, OpaqueQuestion, PublicAnswer>(
            "opaque-input",
            state => new OpaqueQuestion(state.Reference),
            (state, answer) => state with { Answer = answer.Text }
        );
        var complete = PipelineNodes.Complete(new PublicCompletion("complete"));
        var pipeline = Pipeline
            .Start(start, "opaque-interaction")
            .Route(start.Success, interaction, "ask")
            .Route(_ => true, interaction, complete, "answered")
            .Build(complete);
        var reference = new NonSerializableReference();
        var interactions = new PipelineInteractionHandlers().Handle(
            interaction,
            (context, _) =>
            {
                context.Request.Reference.Should().BeSameAs(reference);
                return ValueTask.FromResult(new PublicAnswer("continue"));
            }
        );

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new PublicState(1, reference),
            new PipelineRunOptions(Interactions: interactions),
            CancellationToken.None
        );

        result.State.Answer.Should().Be("continue");
    }

    [Fact]
    public void PublicRuntimeSurface_ExposesNoMafTypes()
    {
        var runtimeTypes = new[]
        {
            typeof(PipelineRunner),
            typeof(PipelineRunOptions),
            typeof(PipelineRunResult<>),
            typeof(PipelineInteractionHandlers),
            typeof(PipelineInteractionContext<,>),
        };

        runtimeTypes
            .SelectMany(type => type.GetMembers().SelectMany(PublicMemberTypes))
            .Where(type => type.Assembly.GetName().Name?.StartsWith("Microsoft.Agents") == true)
            .Should()
            .BeEmpty();
    }

    private static IEnumerable<Type> PublicMemberTypes(System.Reflection.MemberInfo member)
    {
        if (member is System.Reflection.MethodInfo method)
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
        else if (member is System.Reflection.PropertyInfo property)
        {
            yield return property.PropertyType;
        }
    }

    private sealed class RecordingObserver : IPipelineObserver
    {
        public ConcurrentQueue<PipelineObservation> Observations { get; } = new();

        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            Observations.Enqueue(observation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TextChatClient(string responseText) : IChatClient
    {
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
            var response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [new TextContent(responseText)])
            )
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "test-model",
            };
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

public sealed record PublicState(
    int Count,
    NonSerializableReference Reference,
    string? Answer = null
);

public sealed class NonSerializableReference
{
    public Action Callback { get; } = () => { };
}

public sealed record PublicQuestion(string Text);

public sealed record OpaqueQuestion(NonSerializableReference Reference);

public sealed record PublicAnswer(string Text);

public sealed record DurablePublicState(int Count);

[PipelineStage("durable-public-stage")]
public sealed partial class DurablePublicStage
{
    public ValueTask<DurablePublicState> ExecuteAsync(
        DurablePublicState state,
        CancellationToken _
    ) => ValueTask.FromResult(state with { Count = state.Count + 1 });
}

public sealed class PublicCompletion(string id) : IPipelineCompletion<PublicState>
{
    public string Id => id;

    public string Summarize(PublicState state) => id;
}

[PipelineStage("public-increment")]
public sealed partial class PublicIncrementStage
{
    public ValueTask<PublicState> ExecuteAsync(PublicState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Count = state.Count + 1 });
}

[PipelineStage("public-start")]
public sealed partial class PublicStartStage
{
    public ValueTask<Outcome<PublicState>> ExecuteAsync(PublicState state, CancellationToken _) =>
        ValueTask.FromResult<Outcome<PublicState>>(new Outcome<PublicState>.Success(state));
}

[PipelineStage("public-between")]
public sealed partial class PublicBetweenStage
{
    public ValueTask<Outcome<PublicState>> ExecuteAsync(PublicState state, CancellationToken _) =>
        ValueTask.FromResult<Outcome<PublicState>>(new Outcome<PublicState>.Success(state));
}
