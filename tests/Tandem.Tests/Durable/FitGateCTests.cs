using FluentAssertions;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;

namespace Tandem.Tests.Durable;

/// <summary>
/// F-C: durable continuation, no replay of completed executors, and events.
/// </summary>
[Collection("Durable Task Scheduler")]
public sealed class FitGateCTests
{
    [Fact]
    public async Task RestartedHost_ResumesRunWithoutRepeatingCompletedExecutor()
    {
        DtsFixture.EnsureReachable();

        using var runDirectory = new TemporaryDirectory();
        var firstInvocationPath = Path.Combine(runDirectory.Path, "first-invocations.txt");
        var secondInvocationPath = Path.Combine(runDirectory.Path, "second-invocations.txt");

        var workflow = BuildResumeWorkflow(
            firstInvocationPath,
            secondInvocationPath,
            "fit-c-resume"
        );
        var runId = "fit-c-resume-" + Guid.NewGuid().ToString("N");

        await using (
            var firstHost = await DurableHost.StartAsync(options => options.AddWorkflow(workflow))
        )
        {
            var run = (IAwaitableWorkflowRun)
                await firstHost.WorkflowClient.RunAsync(workflow, "start", runId);

            await WaitForFileAsync(secondInvocationPath);
            run.RunId.Should().Be(runId);
        }

        var restartedWorkflow = BuildResumeWorkflow(
            firstInvocationPath,
            secondInvocationPath,
            "fit-c-resume"
        );

        await using var restartedHost = await DurableHost.StartAsync(options =>
            options.AddWorkflow(restartedWorkflow)
        );

        var existingRun = await restartedHost.DurableTaskClient.GetInstanceAsync(runId);
        existingRun.Should().NotBeNull("the durable run must survive host shutdown");

        var completed = await restartedHost.DurableTaskClient.WaitForInstanceCompletionAsync(
            runId,
            getInputsAndOutputs: true,
            CancellationToken.None
        );
        completed.Should().NotBeNull();
        completed!.RuntimeStatus.Should().Be(OrchestrationRuntimeStatus.Completed);

        File.ReadAllLines(firstInvocationPath).Should().ContainSingle();
        File.ReadAllLines(secondInvocationPath).Should().HaveCount(2);
    }

    [Fact]
    public async Task CustomWorkflowEvent_IsVisibleFromDurableStream()
    {
        DtsFixture.EnsureReachable();

        var executor = new FunctionExecutor<string>(
            "event-source",
            async (input, context, cancellationToken) =>
            {
                await context.AddEventAsync(
                    new FitGateWorkflowEvent("durable-event"),
                    cancellationToken
                );
            }
        );
        var binding = executor.BindExecutor();
        var workflow = new WorkflowBuilder(binding)
            .WithName("fit-c-event-" + Guid.NewGuid().ToString("N"))
            .Build();

        await using var host = await DurableHost.StartAsync(options =>
            options.AddWorkflow(workflow)
        );

        var run = await host.WorkflowClient.StreamAsync(
            workflow,
            "start",
            "fit-c-event-" + Guid.NewGuid().ToString("N")
        );
        var events = await DurableWorkflowTestHelpers.WatchToCompletionAsync(run);

        DurableWorkflowTestHelpers.AssertCompleted(events);
        events
            .OfType<FitGateWorkflowEvent>()
            .Select(evt => evt.Message)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("durable-event");
    }

    private static Workflow BuildResumeWorkflow(
        string firstInvocationPath,
        string secondInvocationPath,
        string workflowName
    )
    {
        var first = new FunctionExecutor<string, string>(
            "resume-first",
            (input, context, cancellationToken) =>
            {
                File.AppendAllText(firstInvocationPath, "invoked\n");
                return ValueTask.FromResult("continue");
            }
        );
        var second = new FunctionExecutor<string>(
            "resume-second",
            async (input, context, cancellationToken) =>
            {
                File.AppendAllText(secondInvocationPath, "invoked\n");
                var invocationCount = File.ReadAllLines(secondInvocationPath).Length;

                if (invocationCount == 1)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }
        );

        var firstBinding = first.BindExecutor();
        var secondBinding = second.BindExecutor();
        return new WorkflowBuilder(firstBinding)
            .WithName(workflowName)
            .AddEdge(firstBinding, secondBinding)
            .Build();
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-fit-c-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for a test fixture.
            }
        }
    }

    private sealed class FitGateWorkflowEvent(string message) : WorkflowEvent(message)
    {
        public string Message { get; } = message;
    }
}
