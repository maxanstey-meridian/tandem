using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.OpenAICompatible;

namespace Tandem.Tests.Infrastructure;

public sealed class RequestDeadlineChatClientTests
{
    [Fact]
    public async Task Idle_timeout_retries_only_the_failed_request_and_discards_partial_output()
    {
        var inner = SlowClient(false, recover: true);
        var client = new StreamRetryChatClient(
            new RequestDeadlineChatClient(
                inner,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(50)
            ),
            2,
            TimeSpan.Zero
        );
        var output = await Read(client);
        output.Should().Be("recovered");
        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Total_deadline_stops_a_stream_that_keeps_producing_updates()
    {
        var inner = SlowClient(true);
        var client = new StreamRetryChatClient(
            new RequestDeadlineChatClient(
                inner,
                TimeSpan.FromMilliseconds(120),
                TimeSpan.FromSeconds(1)
            ),
            2,
            TimeSpan.Zero
        );
        await FluentActions.Awaiting(() => Read(client)).Should().ThrowAsync<TimeoutException>();
        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Parent_cancellation_does_not_retry()
    {
        var inner = SlowClient(false);
        var client = new StreamRetryChatClient(
            new RequestDeadlineChatClient(inner, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)),
            2,
            TimeSpan.Zero
        );
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await FluentActions
            .Awaiting(() => Read(client, cancel.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        inner.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Nonstreaming_deadline_is_retryable()
    {
        var inner = SlowClient(false);
        var client = new StreamRetryChatClient(
            new RequestDeadlineChatClient(inner, TimeSpan.FromMilliseconds(50)),
            2,
            TimeSpan.Zero
        );
        await FluentActions
            .Awaiting(async () => await client.GetResponseAsync([new(ChatRole.User, "hi")]))
            .Should()
            .ThrowAsync<TimeoutException>();
        inner.CallCount.Should().Be(2);
    }

    private static async Task<string> Read(IChatClient client, CancellationToken token = default)
    {
        var text = "";
        await foreach (
            var item in client.GetStreamingResponseAsync(
                [new(ChatRole.User, "hi")],
                cancellationToken: token
            )
        )
        {
            {
                text += item.Text;
            }
        }

        return text;
    }

    private static TestChatClient SlowClient(bool trickle, bool recover = false)
    {
        var calls = 0;
        return new TestChatClient
        {
            Respond = async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new ChatResponse();
            },
            Stream = (_, _, cancellationToken) =>
            {
                calls++;
                return Slow(trickle, recover && calls > 1, cancellationToken);
            },
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Slow(
        bool trickle,
        bool recovered,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (recovered)
        {
            yield return new(ChatRole.Assistant, "recovered");
            yield break;
        }
        yield return new(ChatRole.Assistant, "partial");
        while (true)
        {
            await Task.Delay(trickle ? 10 : Timeout.Infinite, cancellationToken);
            yield return new(ChatRole.Assistant, "tick");
        }
    }
}
