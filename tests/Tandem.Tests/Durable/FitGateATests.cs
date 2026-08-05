using FluentAssertions;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;

namespace Tandem.Tests.Durable;

/// <summary>
/// F-A: Packages, DTS emulator, trivial durable run.
/// Proves a single-executor durable workflow runs to completion via
/// IWorkflowClient.StreamAsync + WatchStreamAsync. Confirms
/// ConfigureDurableWorkflows registration, IWorkflowClient resolution,
/// and the IStreamingWorkflowRun event stream.
/// </summary>
[Collection("Durable Task Scheduler")]
public sealed class FitGateATests
{
    [Fact]
    public async Task TrivialDurableWorkflow_CompletesViaStreaming()
    {
        DtsFixture.EnsureReachable();

        var executor = new FunctionExecutor<string>(
            "echo",
            (input, context, ct) => ValueTask.CompletedTask
        );

        var startBinding = executor.BindExecutor();
        var workflow = new WorkflowBuilder(startBinding).WithName("fit-a-echo").Build();

        await using var host = await DurableHost.StartAsync(options =>
            options.AddWorkflow(workflow)
        );

        var client = host.WorkflowClient;
        var runId = "fit-a-" + Guid.NewGuid().ToString("N");

        var run = await client.StreamAsync(workflow, "hello", runId, CancellationToken.None);

        var events = new List<WorkflowEvent>();
        await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
        {
            events.Add(evt);
        }

        var completed = events.OfType<DurableWorkflowCompletedEvent>().SingleOrDefault();
        completed.Should().NotBeNull("the workflow must produce a completion event");
    }

    [Fact]
    public void AddSwitch_ExistsAsExtensionMethodOnWorkflowBuilder()
    {
        // Compile-time proof that AddSwitch is accessible as an extension
        // method on WorkflowBuilder with the expected signature:
        //   AddSwitch(WorkflowBuilder, ExecutorBinding, Action<SwitchBuilder>)
        var method = typeof(WorkflowBuilderExtensions).GetMethod(
            "AddSwitch",
            types: [typeof(WorkflowBuilder), typeof(ExecutorBinding), typeof(Action<SwitchBuilder>)]
        );

        method
            .Should()
            .NotBeNull(
                "WorkflowBuilderExtensions.AddSwitch(WorkflowBuilder, ExecutorBinding, Action<SwitchBuilder>) must exist"
            );
    }
}
