using FluentAssertions;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class InMemoryExternalRequestBrokerTests
{
    [Fact]
    public async Task WaitAsync_PublishesPendingRequestAndReturnsMatchingAnswer()
    {
        await using var broker = new InMemoryExternalRequestBroker();
        var request = Request();

        var wait = broker.WaitAsync(request, CancellationToken.None).AsTask();
        var pending = broker.PendingRequests.Should().ContainSingle().Which;
        broker.Answer(
            new ExternalRequestAnswer(
                request.RunId,
                request.RequestId,
                typeof(ProbeAnswer),
                new ProbeAnswer("continue")
            )
        );

        pending.Should().Be(request);
        var answer = await wait;
        answer.RequestId.Should().Be(request.RequestId);
        answer.Value.Should().Be(new ProbeAnswer("continue"));
        broker.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitAsync_RejectsDuplicatePendingIdentity()
    {
        await using var broker = new InMemoryExternalRequestBroker();
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        var first = broker.WaitAsync(request, cancellation.Token).AsTask();
        broker.PendingRequests.Should().ContainSingle();

        var act = () => broker.WaitAsync(request, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already pending*");
        await cancellation.CancelAsync();
        var firstAct = async () => await first;
        await firstAct.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Answer_RejectsWrongDuplicateAndLateAnswers()
    {
        await using var broker = new InMemoryExternalRequestBroker();
        var request = Request();
        var wait = broker.WaitAsync(request, CancellationToken.None).AsTask();
        broker.PendingRequests.Should().ContainSingle();
        var wrong = new ExternalRequestAnswer(
            Guid.CreateVersion7(),
            request.RequestId,
            typeof(ProbeAnswer),
            new ProbeAnswer("wrong")
        );
        var answer = new ExternalRequestAnswer(
            request.RunId,
            request.RequestId,
            typeof(ProbeAnswer),
            new ProbeAnswer("continue")
        );

        var wrongAct = () => broker.Answer(wrong);
        wrongAct.Should().Throw<InvalidOperationException>().WithMessage("*not pending*");
        broker.Answer(answer);
        await wait;

        var duplicateAct = () => broker.Answer(answer);
        duplicateAct.Should().Throw<InvalidOperationException>().WithMessage("*not pending*");
    }

    [Fact]
    public async Task Cancellation_RemovesPendingWaiter()
    {
        await using var broker = new InMemoryExternalRequestBroker();
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        var wait = broker.WaitAsync(request, cancellation.Token).AsTask();
        broker.PendingRequests.Should().ContainSingle();

        await cancellation.CancelAsync();

        var act = async () => await wait;
        await act.Should().ThrowAsync<OperationCanceledException>();
        broker.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Answers_RemainIsolatedAcrossRunsAndRequests()
    {
        await using var broker = new InMemoryExternalRequestBroker();
        var firstRequest = Request();
        var secondRequest = Request();
        var first = broker.WaitAsync(firstRequest, CancellationToken.None).AsTask();
        var second = broker.WaitAsync(secondRequest, CancellationToken.None).AsTask();

        broker.Answer(Answer(secondRequest, "second"));

        (await second).Value.Should().Be(new ProbeAnswer("second"));
        first.IsCompleted.Should().BeFalse();
        broker.PendingRequests.Should().ContainSingle().Which.Should().Be(firstRequest);

        broker.Answer(Answer(firstRequest, "first"));
        (await first).Value.Should().Be(new ProbeAnswer("first"));
        broker.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_CancelsEveryPendingWaiter()
    {
        var broker = new InMemoryExternalRequestBroker();
        var first = broker.WaitAsync(Request(), CancellationToken.None).AsTask();
        var second = broker.WaitAsync(Request(), CancellationToken.None).AsTask();
        broker.PendingRequests.Should().HaveCount(2);

        await broker.DisposeAsync();

        var firstAct = async () => await first;
        var secondAct = async () => await second;
        await firstAct.Should().ThrowAsync<OperationCanceledException>();
        await secondAct.Should().ThrowAsync<OperationCanceledException>();
        broker.PendingCount.Should().Be(0);
    }

    private static PendingExternalRequest Request() =>
        new(
            Guid.CreateVersion7(),
            Guid.NewGuid().ToString("N"),
            "human-input",
            typeof(ProbeQuestion),
            typeof(ProbeAnswer),
            new ProbeQuestion("Continue?")
        );

    private static ExternalRequestAnswer Answer(PendingExternalRequest request, string text) =>
        new(request.RunId, request.RequestId, typeof(ProbeAnswer), new ProbeAnswer(text));
}
