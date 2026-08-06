using FluentAssertions;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;
using Tandem.Domain;
using Tandem.Tests.Composition;

namespace Tandem.Tests.Durable;

[Collection("Durable Task Scheduler")]
public sealed class ClosedGenericMessageProofTests
{
    [Fact]
    public async Task DurableAdapter_RoundTripsUnrelatedClosedGenericMessage()
    {
        DtsFixture.EnsureReachable();
        var terminal = new TinyDebateTerminal();
        var binding = terminal.BindExecutor();
        var workflow = new WorkflowBuilder(binding)
            .WithName("closed-generic-debate-proof")
            .WithOutputFrom(binding)
            .Build();
        var input = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new DebateState("Question", [], 0, null)
        );
        var runId = "closed-generic-" + Guid.NewGuid().ToString("N");

        await using var host = await DurableHost.StartAsync(options =>
            options.AddWorkflow(workflow)
        );
        var run = (IAwaitableWorkflowRun)await host.WorkflowClient.RunAsync(workflow, input, runId);
        var output = await run.WaitForCompletionAsync<PipelineMessage<DebateState>>();
        var instance = await host.DurableTaskClient.WaitForInstanceCompletionAsync(
            runId,
            getInputsAndOutputs: true,
            CancellationToken.None
        );

        instance.Should().NotBeNull();
        instance!.RuntimeStatus.Should().Be(OrchestrationRuntimeStatus.Completed);
        output.Should().NotBeNull();
        output!.GetType().Should().Be<PipelineMessage<DebateState>>();
        output.State.Verdict.Should().Be(new Verdict("Affirmed", "durable verdict"));
        instance.SerializedOutput.Should().Contain("durable verdict");
        instance.SerializedOutput.Should().Contain("Question");
    }

    private sealed class TinyDebateTerminal()
        : Executor<PipelineMessage<DebateState>, PipelineMessage<DebateState>>("tiny-terminal")
    {
        public override ValueTask<PipelineMessage<DebateState>> HandleAsync(
            PipelineMessage<DebateState> message,
            IWorkflowContext context,
            CancellationToken cancellationToken
        ) =>
            ValueTask.FromResult(
                message with
                {
                    State = message.State with
                    {
                        Verdict = new Verdict("Affirmed", "durable verdict"),
                    },
                }
            );
    }
}
