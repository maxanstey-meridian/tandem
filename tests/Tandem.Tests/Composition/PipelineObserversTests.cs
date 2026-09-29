using FluentAssertions;

namespace Tandem.Tests.Composition;

public sealed class PipelineObserversTests
{
    [Fact]
    public async Task Compose_ObservesInOrderSkippingNulls()
    {
        var seen = new List<string>();
        var composed = PipelineObservers.Compose(
            new InlineObserver(_ => seen.Add("first")),
            null,
            new InlineObserver(_ => seen.Add("second"))
        );

        await composed!.ObserveAsync(
            new PipelineStepStarted(Guid.CreateVersion7(), "step"),
            CancellationToken.None
        );

        seen.Should().Equal("first", "second");
    }

    [Fact]
    public void Compose_IsAPersistenceObserverOnlyWhenTheFirstObserverIsOne()
    {
        var persistence = new RecordingObserver([]);
        var live = new InlineObserver(_ => { });

        PipelineObservers.Compose().Should().BeNull();
        PipelineObservers.Compose(null, live).Should().BeSameAs(live);
        PipelineObservers
            .Compose(persistence, live)
            .Should()
            .BeAssignableTo<IPipelinePersistenceObserver>();
        PipelineObservers
            .Compose(live, persistence)
            .Should()
            .NotBeAssignableTo<IPipelinePersistenceObserver>();
    }
}
