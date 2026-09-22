using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Tandem.Terminal.Tests;

public sealed class CollectionObservationTests
{
    [Fact]
    public async Task OverlappingAgentVisitsRemainDistinctThroughCompletionAndStreaming()
    {
        using var client = new ControlledClient();
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var agent = Agent
            .Create<string>("rewrite", "Rewrite.", client)
            .WithMessage(value => value)
            .WithJsonOutput(
                new AgentJsonOutputDefinition<string>(
                    schema.RootElement,
                    "Return claim.",
                    _ => [],
                    "claim"
                ),
                (_, value) => value.GetProperty("claim").GetString()!
            )
            .Build();
        var collect = PipelineCollection.Create<string[], string, string>(
            "collect",
            values => values,
            [agent],
            (value, scope, _) => scope.RunAsync(agent, value),
            (_, values) => values.ToArray(),
            2
        );
        var graph = Pipeline.Start(collect, "observations").Build(collect);
        var runId = Guid.NewGuid();
        var model = new TerminalModel(
            "observations",
            runId,
            TimeProvider.System,
            100,
            10000,
            null,
            null
        );
        var observer = new Observer(model, client.ReleaseSecond);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new PipelineRunner().RunAsync(
            graph,
            ["first", "second"],
            new(RunId: runId, Observer: observer),
            timeout.Token
        );
        result.State.Should().Equal("first", "second");
        observer.DuringSecond.Should().NotBeNull();
        observer.DuringSecond!.ActiveStep.Should().Be("collect/rewrite");
        observer
            .DuringSecond.Visits.Count(visit =>
                visit.StepId == "collect/rewrite" && visit.CompletedAt is null
            )
            .Should()
            .Be(1);
        var visits = model
            .Snapshot()
            .Visits.Where(visit => visit.StepId == "collect/rewrite")
            .ToArray();
        visits
            .Should()
            .HaveCount(2)
            .And.OnlyContain(visit => visit.CompletedAt != null && visit.VisitId != null);
        visits.Select(visit => visit.VisitId).Should().OnlyHaveUniqueItems();
        var text = model
            .Snapshot()
            .Transcript.Where(entry => entry.Kind == TranscriptKind.Text)
            .ToArray();
        text.Should().HaveCount(2);
        text.Select(entry => entry.VisitId)
            .Should()
            .BeEquivalentTo(visits.Select(visit => visit.VisitId));
        text.Select(entry =>
                JsonDocument.Parse(entry.Text).RootElement.GetProperty("claim").GetString()
            )
            .Should()
            .BeEquivalentTo("first", "second");
    }

    private sealed class Observer(TerminalModel model, TaskCompletionSource releaseSecond)
        : IPipelineObserver
    {
        public TerminalSnapshot? DuringSecond { get; private set; }

        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            model.Apply(observation);
            if (
                observation is PipelineStepCompleted { StepId: "collect/rewrite" }
                && DuringSecond is null
            )
            {
                DuringSecond = model.Snapshot();
                releaseSecond.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledClient : IChatClient
    {
        private int _started;
        private readonly TaskCompletionSource _both = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource ReleaseSecond { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            var text = messages.Last(message => message.Role == ChatRole.User).Text;
            if (Interlocked.Increment(ref _started) == 2)
            {
                _both.TrySetResult();
            }
            await _both.Task.WaitAsync(cancellationToken);
            if (text == "second")
            {
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }
            yield return new(ChatRole.Assistant, JsonSerializer.Serialize(new { claim = text }));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
