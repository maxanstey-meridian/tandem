using FluentAssertions;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tandem.Application;
using Tandem.Domain;
using Tandem.Infrastructure;

namespace Tandem.Tests.Durable;

/// <summary>
/// Real-model lifecycle proof (plan 02 lines 1015-1031). Runs the full
/// delivery lifecycle against the real DTS emulator and a real model.
///
/// prepare -> executor asks planner -> planner proceeds
/// -> executor edits and submits report -> candidate captured
/// -> packet verification passes -> reviewer accepts -> Ready
///
/// This test is marked as a manual proof — it requires the DTS emulator running
/// and a valid API key in the environment. It skips when the emulator or key
/// is not available.
/// </summary>
[Collection("Durable Task Scheduler")]
public sealed class RealModelLifecycleProofTests
{
    [Fact]
    public async Task Delivery_FullLifecycle_ReachesReady()
    {
        DtsFixture.EnsureReachable();
        EnsureApiKeyAvailable();

        // Use the real Tandem home to resolve provider/profile config.
        var tandemHome = TandemHomeResolver.Resolve();
        var config = new TandemConfigurationLoader().Load(tandemHome);
        config
            .Profiles.Keys.Should()
            .Contain(
                new[] { "implementation", "planning", "review" },
                $"config at {Path.Combine(tandemHome, "config.json")} must define all three profiles"
            );
        var chatClientFactory = new RealChatClientFactory(config);
        var capabilities = TestDeliveryCapabilities.Create();

        var tandemExePath = ResolveTandemExePath();
        var composition = new DeliveryComposition(
            new DeliveryStepsFactory(
                new AgentRuntime(tandemHome, tandemExePath),
                chatClientFactory.Build,
                chatClientFactory.ResolveProfile,
                new DeliveryDiffAcquisition(new GitProcess()),
                new WorkspacePreparation(new GitProcess()),
                new GitProcess(),
                capabilities.AskPlanner,
                capabilities.SubmitReport,
                capabilities.WriteCheckpoint
            )
        );
        var reasoningUpdates = 0;
        var runtimeRunId = Guid.CreateVersion7();
        using var agentUpdates = AgentUpdates.Observe(
            runtimeRunId,
            (_, _, update) =>
                Interlocked.Add(ref reasoningUpdates, update is AgentUpdate.Reasoning ? 1 : 0)
        );
        var pipeline = composition.Build(new PipelineBuildContext());
        var workflow = PipelineMafBridge.GetWorkflow(pipeline);

        var runId = "real-model-" + Guid.NewGuid().ToString("N");

        // Use the example todo-api repo as the target workspace.
        var exampleRepoPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "examples",
                "01-todo-api",
                "repo"
            )
        );
        if (!Directory.Exists(exampleRepoPath))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"Example repo not found at {exampleRepoPath}. Run from the Tandem root."
            );
        }

        var workspacePath = Path.Combine(tandemHome, "runs", runId, "workspace");
        Directory.CreateDirectory(Path.Combine(tandemHome, "runs", runId));
        var packet = new Packet(
            Title: "Tandem real-model proof",
            Repository: exampleRepoPath,
            Base: "main",
            Outcomes:
            [
                new Outcome(
                    "mark-complete",
                    "Add a `markComplete(id)` method to `TodoService` that sets `completed` to `true` on the matching todo. It should return the updated todo or `undefined` if not found."
                ),
                new Outcome(
                    "filter-by-status",
                    "Add a `listByStatus(completed: boolean)` method to `TodoService` that returns todos filtered by their `completed` flag."
                ),
            ],
            Verification:
            [
                "grep -q 'markComplete' src/service.ts",
                "grep -q 'listByStatus' src/service.ts",
            ],
            Constraints:
            [
                "Do not change existing method signatures.",
                "Do not add new dependencies.",
                "Do not modify src/store.ts.",
            ],
            ImplementationContext: "Inspect the existing code in src/ before making changes."
        );

        var initialMessage = new PipelineMessage<DeliveryState>(
            PipelineRuntime.Create(runtimeRunId),
            DeliveryState.Create(packet, "", workspacePath)
        );

        var connectionString = DtsFixture.ConnectionString;

        PipelineMessage<DeliveryState>? pipelineOutput = null;

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.ConfigureDurableWorkflows(
                    options => options.AddWorkflow(workflow),
                    workerBuilder => workerBuilder.UseDurableTaskScheduler(connectionString),
                    clientBuilder => clientBuilder.UseDurableTaskScheduler(connectionString)
                );
            })
            .Build();

        await host.StartAsync();
        try
        {
            var workflowClient = host.Services.GetRequiredService<IWorkflowClient>();
            var durableTaskClient =
                host.Services.GetRequiredService<Microsoft.DurableTask.Client.DurableTaskClient>();

            // Use RunAsync + WaitForCompletionAsync because the pinned preview's
            // streaming deserializer throws for routed WorkflowOutputEvent shapes.
            var run = (IAwaitableWorkflowRun)
                await workflowClient.RunAsync(workflow, initialMessage, runId);

            try
            {
                pipelineOutput = await run.WaitForCompletionAsync<PipelineMessage<DeliveryState>>();
            }
            catch (Exception ex)
            {
                // Read the failure details for diagnostics.
                var failedInstance = await durableTaskClient.GetInstanceAsync(
                    runId,
                    getInputsAndOutputs: true
                );
                var failureDetails = failedInstance?.FailureDetails?.ToString() ?? "(no details)";
                var failedOutput = failedInstance?.SerializedOutput ?? "(none)";
                throw new InvalidOperationException(
                    $"Workflow failed. Failure={failureDetails}, Output={failedOutput}, Inner={ex.Message}"
                );
            }

            // Confirm the durable instance completed after returning the typed result.
            var instance = await durableTaskClient.GetInstanceAsync(
                runId,
                getInputsAndOutputs: true
            );
            instance.Should().NotBeNull("the durable run must complete");
            instance!
                .RuntimeStatus.Should()
                .Be(Microsoft.DurableTask.Client.OrchestrationRuntimeStatus.Completed);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }

        pipelineOutput.Should().NotBeNull("the workflow must produce a terminal output");
        pipelineOutput!.State.Status.Should().Be(Tandem.Domain.RunStatus.Ready);
        pipelineOutput.LatestOutcome.Should().NotBeNull("the terminal output must have an outcome");
        pipelineOutput.LatestOutcome!.Kind.Should().Be(OutcomeKinds.RunReady);
        reasoningUpdates
            .Should()
            .BeGreaterThan(0, "the configured reasoning stream must reach operators");
    }

    private static void EnsureApiKeyAvailable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "OPENROUTER_API_KEY is not set. This test requires a real model API key."
            );
        }
    }

    private static string ResolveTandemExePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tandem"),
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "Tandem.Tool",
                "bin",
                "Debug",
                "net10.0",
                "Tandem.Tool"
            ),
        };

        foreach (var candidate in candidates)
        {
            var resolved = Path.GetFullPath(candidate);
            if (File.Exists(resolved))
            {
                return resolved;
            }
        }

        throw new FileNotFoundException(
            "Could not locate the Tandem executable. Ensure the Tandem project is built."
        );
    }
}

internal sealed class RealChatClientFactory(TandemConfig config)
{
    private readonly Dictionary<string, IChatClient> _cache = new();

    public IChatClient Build(string profileName)
    {
        if (_cache.TryGetValue(profileName, out var cached))
        {
            return cached;
        }

        var profile = ResolveProfile(profileName);
        var apiKey = EnvironmentApiKeyReader.Read(
            config.Providers[config.Profiles[profileName].Provider].ApiKeyEnvironmentVariable
        );
        var client = new Tandem.Infrastructure.ChatClientBuilder().Build(profile, apiKey);
        _cache[profileName] = client;
        return client;
    }

    public ResolvedProfile ResolveProfile(string profileName)
    {
        if (!config.Profiles.TryGetValue(profileName, out var profileConfig))
        {
            throw new ProfileResolutionException($"Profile '{profileName}' is not configured.");
        }

        if (!config.Providers.TryGetValue(profileConfig.Provider, out var providerConfig))
        {
            throw new ProfileResolutionException(
                $"Provider '{profileConfig.Provider}' referenced by profile '{profileName}' is not configured."
            );
        }

        var apiKey = EnvironmentApiKeyReader.Read(providerConfig.ApiKeyEnvironmentVariable);
        return new ProfileResolver().Resolve(config, profileName, apiKey);
    }
}
