using FluentAssertions;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Infrastructure;

public sealed class AgentStreamIdleTimeoutTests
{
    [Fact]
    public async Task Fails_when_the_model_stream_stops_producing_updates()
    {
        var read = () =>
            ReadAll(
                AgentBlock<int>.WithIdleTimeout(
                    StallAfterOne(),
                    TimeSpan.FromMilliseconds(20),
                    CancellationToken.None
                )
            );

        var exception = await read.Should().ThrowAsync<TimeoutException>();
        exception.Which.Message.Should().Contain("no update");
    }

    [Fact]
    public async Task Resets_the_idle_clock_after_each_update()
    {
        var values = await ReadAll(
            AgentBlock<int>.WithIdleTimeout(
                UpdatesWithinTimeout(),
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None
            )
        );

        values.Should().Equal(1, 2, 3);
    }

    private static async IAsyncEnumerable<int> StallAfterOne(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
    )
    {
        yield return 1;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<int> UpdatesWithinTimeout()
    {
        for (var value = 1; value <= 3; value++)
        {
            await Task.Delay(10);
            yield return value;
        }
    }

    private static async Task<IReadOnlyList<T>> ReadAll<T>(IAsyncEnumerable<T> source)
    {
        var values = new List<T>();
        await foreach (var value in source)
        {
            values.Add(value);
        }
        return values;
    }
}
