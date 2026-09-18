using FluentAssertions;

namespace Tandem.Tests.Composition;

public sealed class ParallelAcceptanceTests
{
    [Fact]
    public async Task AcceptanceExcludesOtherObservationsButAllowsItsOwnObservations()
    {
        var observations = new List<string>();
        var observer = new Observer(observations);
        var context = new PipelineRunContext(Guid.CreateVersion7(), observer, new Acceptance());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = context
            .ExecuteAsync(
                async token =>
                {
                    entered.SetResult();
                    await release.Task.WaitAsync(token);
                    await context.ObserveAsync(
                        new PipelineStepStarted(context.RunId, "inside"),
                        token
                    );
                    return 42;
                },
                timeout.Token
            )
            .AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        var outside = context
            .ObserveAsync(new PipelineStepStarted(context.RunId, "outside"), timeout.Token)
            .AsTask();
        var outsideWasQueued = !outside.IsCompleted;
        release.SetResult();
        (await accepted).Should().Be(42);
        await outside;
        outsideWasQueued.Should().BeTrue();
        observations.Should().Equal("inside", "outside");
    }

    private sealed class Acceptance : IPipelineAcceptanceUnitOfWork
    {
        public ValueTask<T> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken
        ) => operation(cancellationToken);
    }

    private sealed class Observer(List<string> observations) : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            if (observation is PipelineStepStarted started)
            {
                observations.Add(started.StepId);
            }
            return ValueTask.CompletedTask;
        }
    }
}
