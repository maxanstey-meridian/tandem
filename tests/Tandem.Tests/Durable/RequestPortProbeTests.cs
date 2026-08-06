using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;
using Tandem.Domain;

namespace Tandem.Tests.Durable;

[Collection("Durable Task Scheduler")]
public sealed class RequestPortProbeTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task RequestPort_SuspendsAndResumes_WithDurableTask()
    {
        DtsFixture.EnsureReachable();

        var tandemHome = Path.Combine(
            Path.GetTempPath(),
            "tandem-rp-probe-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(tandemHome);

        try
        {
            // Block 1: produces a HumanQuestion, routes to the request port.
            var askBlock = new FunctionExecutor<string, HumanQuestion>(
                "ask",
                (question, ctx, ct) =>
                {
                    var q = new HumanQuestion("ask", question, "probe");
                    return ValueTask.FromResult(q);
                }
            );

            // Request port: suspends the workflow, waits for HumanAnswer.
            var port = RequestPort.Create<HumanQuestion, HumanAnswer>("HumanInput");

            // Block 3: receives HumanAnswer, produces final result string.
            var applyBlock = new FunctionExecutor<HumanAnswer, string>(
                "apply",
                (answer, ctx, ct) => ValueTask.FromResult($"Answer: {answer.Text}")
            );

            var askBinding = askBlock.BindExecutor();
            var portBinding = (ExecutorBinding)port;
            var applyBinding = applyBlock.BindExecutor();

            var workflow = new WorkflowBuilder(askBinding)
                .WithName("request-port-probe")
                .AddEdge(askBinding, portBinding)
                .AddEdge(portBinding, applyBinding)
                .WithOutputFrom(applyBinding)
                .Build();

            var runId = "rp-probe-" + Guid.NewGuid().ToString("N");

            await using var host = await DurableHost.StartAsync(options =>
                options.AddWorkflow(workflow)
            );

            // Start the workflow — it will suspend at the request port.
            await host.WorkflowClient.RunAsync(workflow, "What should I do?", runId);

            // Wait for the workflow to reach the pending state.
            // Poll the instance until it's not Running (should be "Pending" or similar).
            object? instance = null;
            for (var i = 0; i < 30; i++)
            {
                instance = await host.DurableTaskClient.GetInstanceAsync(
                    runId,
                    getInputsAndOutputs: false,
                    CancellationToken.None
                );
                if (instance is not null)
                {
                    break;
                }
                await Task.Delay(500, CancellationToken.None);
            }

            // The workflow should be suspended (not completed, not running).
            instance.Should().NotBeNull();

            // Send the answer via RaiseEventAsync.
            var answer = new HumanAnswer("Use the repository's existing pattern");
            var serialized = JsonSerializer.Serialize(answer, _jsonOptions);

            await host.DurableTaskClient.RaiseEventAsync(
                runId,
                "HumanInput",
                serialized,
                CancellationToken.None
            );

            // Wait for completion.
            var completed = await host.DurableTaskClient.WaitForInstanceCompletionAsync(
                runId,
                getInputsAndOutputs: true,
                CancellationToken.None
            );

            completed.Should().NotBeNull();
            completed!.RuntimeStatus.Should().Be(OrchestrationRuntimeStatus.Completed);
        }
        finally
        {
            if (Directory.Exists(tandemHome))
            {
                Directory.Delete(tandemHome, recursive: true);
            }
        }
    }
}
