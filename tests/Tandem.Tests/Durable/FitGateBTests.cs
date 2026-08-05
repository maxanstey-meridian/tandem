using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;

namespace Tandem.Tests.Durable;

/// <summary>
/// F-B: ordered switch routing and cyclic graph execution.
/// </summary>
[Collection("Durable Task Scheduler")]
public sealed class FitGateBTests
{
    [Fact]
    public async Task AddSwitch_InProcessExecutesOnlyTheFirstMatchingCase()
    {
        var invoked = new ConcurrentQueue<string>();
        var source = new FunctionExecutor<RouteMessage, RouteMessage>(
            "source",
            (message, context, ct) => ValueTask.FromResult(message)
        );
        var firstCase = new FunctionExecutor<RouteMessage>(
            "first-case",
            (message, context, ct) =>
            {
                invoked.Enqueue("first-case");
                return ValueTask.CompletedTask;
            }
        );
        var secondCase = new FunctionExecutor<RouteMessage>(
            "second-case",
            (message, context, ct) =>
            {
                invoked.Enqueue("second-case");
                return ValueTask.CompletedTask;
            }
        );

        var sourceBinding = source.BindExecutor();
        var firstCaseBinding = firstCase.BindExecutor();
        var secondCaseBinding = secondCase.BindExecutor();
        var workflow = new WorkflowBuilder(sourceBinding)
            .WithName("fit-b-ordered-switch-in-process")
            .AddSwitch(
                sourceBinding,
                switchBuilder =>
                    switchBuilder
                        .AddCase<RouteMessage>(_ => true, [firstCaseBinding])
                        .AddCase<RouteMessage>(_ => true, [secondCaseBinding])
            )
            .Build();

        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new RouteMessage("both-match"),
            "fit-b-switch-in-process",
            CancellationToken.None
        );

        await foreach (var _ in run.WatchStreamAsync(CancellationToken.None)) { }

        invoked.Should().Equal("first-case");
    }

    [Fact]
    public async Task AddSwitch_DurablePinnedPreviewRunsEveryMatchingCase()
    {
        DtsFixture.EnsureReachable();

        var invoked = new ConcurrentQueue<string>();
        var source = new FunctionExecutor<RouteMessage, RouteMessage>(
            "source",
            (message, context, ct) => ValueTask.FromResult(message)
        );
        var firstCase = new FunctionExecutor<RouteMessage>(
            "first-case",
            (message, context, ct) =>
            {
                invoked.Enqueue("first-case");
                return ValueTask.CompletedTask;
            }
        );
        var secondCase = new FunctionExecutor<RouteMessage>(
            "second-case",
            (message, context, ct) =>
            {
                invoked.Enqueue("second-case");
                return ValueTask.CompletedTask;
            }
        );

        var sourceBinding = source.BindExecutor();
        var firstCaseBinding = firstCase.BindExecutor();
        var secondCaseBinding = secondCase.BindExecutor();
        var workflow = new WorkflowBuilder(sourceBinding)
            .WithName("fit-b-ordered-switch-durable-" + Guid.NewGuid().ToString("N"))
            .AddSwitch(
                sourceBinding,
                switchBuilder =>
                    switchBuilder
                        .AddCase<RouteMessage>(_ => true, [firstCaseBinding])
                        .AddCase<RouteMessage>(_ => true, [secondCaseBinding])
            )
            .Build();

        await using var host = await DurableHost.StartAsync(options =>
            options.AddWorkflow(workflow)
        );

        var run = (IAwaitableWorkflowRun)
            await host.WorkflowClient.RunAsync(
                workflow,
                new RouteMessage("both-match"),
                "fit-b-switch-" + Guid.NewGuid().ToString("N")
            );
        await run.WaitForCompletionAsync<string>();

        // This is the framework-fit failure for the pinned durable preview. Its
        // DurableEdgeMap drops the AddSwitch target selector and emits both
        // matching branches as unconditional fan-out.
        invoked.Should().BeEquivalentTo("first-case", "second-case");
    }

    [Fact]
    public async Task AddSwitch_CanRouteBackToAnEarlierExecutorWithUpdatedMessage()
    {
        DtsFixture.EnsureReachable();

        var firstInputs = new ConcurrentQueue<int>();
        var secondInputs = new ConcurrentQueue<int>();
        var completedInputs = new ConcurrentQueue<int>();

        var first = new FunctionExecutor<CycleMessage, CycleMessage>(
            "first",
            (message, context, ct) =>
            {
                firstInputs.Enqueue(message.Count);
                return ValueTask.FromResult(message with { Count = message.Count + 1 });
            }
        );
        var second = new FunctionExecutor<CycleMessage, CycleMessage>(
            "second",
            (message, context, ct) =>
            {
                secondInputs.Enqueue(message.Count);
                return ValueTask.FromResult(message);
            }
        );
        var complete = new FunctionExecutor<CycleMessage>(
            "complete",
            (message, context, ct) =>
            {
                completedInputs.Enqueue(message.Count);
                return ValueTask.CompletedTask;
            }
        );

        var firstBinding = first.BindExecutor();
        var secondBinding = second.BindExecutor();
        var completeBinding = complete.BindExecutor();
        var workflow = new WorkflowBuilder(firstBinding)
            .WithName("fit-b-cycle-" + Guid.NewGuid().ToString("N"))
            .AddEdge<CycleMessage>(firstBinding, secondBinding, message => message!.Count == 1)
            .AddEdge<CycleMessage>(firstBinding, completeBinding, message => message!.Count >= 2)
            .AddEdge(secondBinding, firstBinding)
            .Build();

        await using var host = await DurableHost.StartAsync(options =>
            options.AddWorkflow(workflow)
        );

        var run = (IAwaitableWorkflowRun)
            await host.WorkflowClient.RunAsync(
                workflow,
                new CycleMessage(0),
                "fit-b-cycle-" + Guid.NewGuid().ToString("N")
            );
        await run.WaitForCompletionAsync<string>();

        firstInputs.Should().Equal(0, 1);
        secondInputs.Should().Equal(1);
        completedInputs.Should().Equal(2);
    }

    private sealed record RouteMessage(string Value);

    private sealed record CycleMessage(int Count);
}
