using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Composition;

public sealed class CollectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public async Task CollectionsApplyOrderedResultsWithBoundedConcurrency(int count)
    {
        var active = 0;
        var peak = 0;
        var applied = 0;
        var sync = new object();
        var collect = PipelineCollection.Create<int[], int, int>(
            "collect",
            state => state,
            [],
            async (value, _, token) =>
            {
                lock (sync)
                {
                    peak = Math.Max(peak, ++active);
                }
                try
                {
                    await Task.Delay(value == 0 ? 60 : 5, token);
                    return value * 2;
                }
                finally
                {
                    lock (sync)
                    {
                        active--;
                    }
                }
            },
            (_, values) =>
            {
                applied++;
                return values.ToArray();
            },
            max: 3
        );
        var done = PipelineNodes.Stage<int[]>("done", (state, _) => ValueTask.FromResult(state));
        var graph = Pipeline
            .Start(collect, "collection")
            .Route(collect, done, "collected")
            .Build(done);
        var result = await new PipelineRunner().RunAsync(
            graph,
            Enumerable.Range(0, count).ToArray()
        );
        result.State.Should().Equal(Enumerable.Range(0, count).Select(value => value * 2));
        active.Should().Be(0);
        peak.Should().Be(Math.Min(count, 3));
        applied.Should().Be(1);
        graph.Inspect().Collections.Single().Max.Should().Be(3);
    }

    [Fact]
    public async Task CanonicalisationAndConditionalRecoveryUseDeclaredAgentsWithoutChildPipelines()
    {
        using var client = new EchoClient();
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        AgentDefinition<Claim> Define(string id) =>
            Agent
                .Create<Claim>(id, "Rewrite.", client)
                .WithMessage(state => state.Text)
                .WithJsonOutput(
                    new AgentJsonOutputDefinition<Claim>(
                        schema.RootElement,
                        "Return a claim.",
                        _ => [],
                        "claim"
                    ),
                    (state, value) => state with { Text = value.GetProperty("claim").GetString()! }
                )
                .Build();
        var canonicaliser = Define("canonicaliser");
        var repairer = canonicaliser;
        var canonicalise = PipelineCollection.Create<Claim[], Claim, Claim>(
            "canonicalise",
            state => state,
            [canonicaliser],
            (item, scope, _) => scope.RunAsync(canonicaliser, item),
            (_, results) => results.ToArray(),
            2
        );
        var recover = PipelineCollection.Create<Claim[], Claim, Claim>(
            "recover",
            state => state,
            [repairer],
            async (item, scope, _) =>
                item.Text.Contains("repair", StringComparison.Ordinal)
                    ? await scope.RunAsync(repairer, item)
                    : item,
            (_, results) => results.ToArray(),
            2
        );
        var done = PipelineNodes.Stage<Claim[]>("done", (state, _) => ValueTask.FromResult(state));
        var graph = Pipeline
            .Start(canonicalise, "claims")
            .Route(canonicalise, recover, "canonicalised")
            .Route(recover, done, "resolved")
            .Build(done);
        var result = await new PipelineRunner().RunAsync(
            graph,
            [new Claim("fine"), new Claim("repair")]
        );
        result.State.Select(claim => claim.Text).Should().Equal("fine!", "repair!!");
        graph
            .Inspect()
            .Collections.SelectMany(scope => scope.AgentIds)
            .Should()
            .Equal("canonicalise/canonicaliser", "recover/canonicaliser");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndCancellationDrainActiveItemsAndNeverApplyPartialResults(bool cancel)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var entered = 0;
        var applied = false;
        var collect = PipelineCollection.Create<int[], int, int>(
            "collect",
            state => state,
            [],
            async (value, _, token) =>
            {
                Interlocked.Increment(ref active);
                if (Interlocked.Increment(ref entered) == 2)
                {
                    ready.SetResult();
                }
                try
                {
                    await ready.Task.WaitAsync(token);
                    if (value == 0)
                    {
                        if (cancel)
                        {
                            cancellation.Cancel();
                        }
                        else
                        {
                            throw new InvalidOperationException("Item failed.");
                        }
                    }
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return value;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            (_, values) =>
            {
                applied = true;
                return values.ToArray();
            },
            2
        );
        var done = PipelineNodes.Stage<int[]>("done", (state, _) => ValueTask.FromResult(state));
        var graph = Pipeline
            .Start(collect, "failure")
            .Route(collect, done, "collected")
            .Build(done);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await new PipelineRunner().RunAsync(
                graph,
                Enumerable.Range(0, 8).ToArray(),
                cancellationToken: cancellation.Token
            )
        );
        active.Should().Be(0);
        entered.Should().Be(2);
        applied.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopedAgentIdsCannotCollideWithParentNodes(bool collectionStarts)
    {
        using var client = new EchoClient();
        var agent = Agent
            .Create<string>("rewrite", "Rewrite.", client)
            .WithMessage(value => value)
            .Build();
        var collect = PipelineCollection.Create<string, string, string>(
            "collect",
            value => [value],
            [agent],
            (value, scope, _) => scope.RunAsync(agent, value),
            (_, values) => values.Single(),
            1
        );
        var collision = PipelineNodes.Stage<string>(
            "collect/rewrite",
            (value, _) => ValueTask.FromResult(value)
        );
        Action build = () =>
        {
            if (collectionStarts)
            {
                Pipeline
                    .Start(collect, "collision")
                    .Route(collect, collision, "next")
                    .Build(collision);
            }
            else
            {
                Pipeline
                    .Start(collision, "collision")
                    .Route(collision, collect, "next")
                    .Build(collect);
            }
        };
        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AnAgentMayHaveTheSameLocalNameAsItsCollection()
    {
        using var client = new EchoClient();
        var agent = Agent
            .Create<string>("rewrite", "Rewrite.", client)
            .WithMessage(value => value)
            .Build();
        var collect = PipelineCollection.Create<string, string, string>(
            "rewrite",
            value => [value],
            [agent],
            (value, scope, _) => scope.RunAsync(agent, value),
            (_, values) => values.Single(),
            1
        );
        var start = PipelineNodes.Stage<string>("start", (value, _) => ValueTask.FromResult(value));
        var graph = Pipeline.Start(start, "scoped").Route(start, collect, "next").Build(collect);
        graph.Inspect().StepIds.Should().Contain("rewrite").And.Contain("rewrite/rewrite");
    }

    private sealed record Claim(string Text);

    private sealed class EchoClient : IChatClient
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
            var text = messages.Last(message => message.Role == ChatRole.User).Text;
            var response = new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    JsonSerializer.Serialize(new { claim = text + "!" })
                )
            )
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "test",
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
