using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tandem;
using Tandem.Actions;
using Tandem.Application;
using Tandem.Delivery;
using Tandem.Domain;
using Tandem.Git;
using Tandem.Infrastructure;
using Tandem.Infrastructure.Dashboard;
using Tandem.Infrastructure.Projection;
using Tandem.Interfaces;
using InfrastructureChatClientBuilder = Tandem.Infrastructure.ChatClientBuilder;

var packetArgument = new Argument<string>("packet-path")
{
    Description = "Path to the packet markdown file.",
};

var debugOption = new Option<bool>("--debug") { Description = "Show stack traces on failure." };

var runCommand = new Command("run", "Run a packet through the Tandem pipeline")
{
    packetArgument,
    debugOption,
};

var runIdArgument = new Argument<string>("run-id") { Description = "Run ID to attach to." };

var attachCommand = new Command("attach", "Attach to a running Tandem run")
{
    runIdArgument,
    debugOption,
};

var branchOption = new Option<string?>("--branch") { Description = "Branch name for publication." };

var publishCommand = new Command("publish", "Publish a Ready candidate as a local branch")
{
    runIdArgument,
    branchOption,
    debugOption,
};

var actionSetArgument = new Argument<string>("action-set")
{
    Description = "Explicitly registered lifecycle action set identity.",
};
var mcpCommand = new Command("mcp", "Host a registered lifecycle action set over stdio")
{
    actionSetArgument,
};
mcpCommand.Hidden = true;

var rootCommand = new RootCommand("Tandem — agentic pipeline runner")
{
    runCommand,
    attachCommand,
    publishCommand,
    mcpCommand,
};

runCommand.SetAction(
    async (ParseResult parseResult, CancellationToken cancellationToken) =>
    {
        var packetPath = parseResult.GetRequiredValue(packetArgument);
        var debug = parseResult.GetValue(debugOption);

        try
        {
            return await RunAsync(packetPath, debug, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
                if (ex.InnerException is not null)
                {
                    Console.Error.WriteLine($"Inner: {ex.InnerException}");
                }
            }
            return 1;
        }
    }
);

attachCommand.SetAction(
    async (ParseResult parseResult, CancellationToken cancellationToken) =>
    {
        var runId = parseResult.GetRequiredValue(runIdArgument);
        var debug = parseResult.GetValue(debugOption);

        try
        {
            return await AttachAsync(runId, debug, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
);

publishCommand.SetAction(
    async (ParseResult parseResult, CancellationToken cancellationToken) =>
    {
        var runId = parseResult.GetRequiredValue(runIdArgument);
        var branch = parseResult.GetValue(branchOption);
        var debug = parseResult.GetValue(debugOption);

        try
        {
            return await PublishAsync(runId, branch, debug, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
);

static (string Home, Guid RunId, string BlockId, string InvocationId) ReadMcpContext()
{
    var tandemHome =
        Environment.GetEnvironmentVariable("TANDEM_HOME")
        ?? throw new InvalidOperationException("TANDEM_HOME is required.");
    var runId =
        Environment.GetEnvironmentVariable("TANDEM_RUN_ID")
        ?? throw new InvalidOperationException("TANDEM_RUN_ID is required.");
    var blockId =
        Environment.GetEnvironmentVariable("TANDEM_BLOCK_ID")
        ?? throw new InvalidOperationException("TANDEM_BLOCK_ID is required.");
    var invocationId =
        Environment.GetEnvironmentVariable("TANDEM_INVOCATION_ID")
        ?? throw new InvalidOperationException("TANDEM_INVOCATION_ID is required.");

    return (tandemHome, Guid.Parse(runId), blockId, invocationId);
}

mcpCommand.SetAction(
    async (ParseResult parseResult, CancellationToken cancellationToken) =>
    {
        var context = ReadMcpContext();
        await using var provider = BuildDeliveryServices(context.Home, config: null);
        var actionSets = provider.GetRequiredService<LifecycleActionSetRegistry>();
        await LifecycleMcpHost.RunAsync(
            actionSets,
            parseResult.GetRequiredValue(actionSetArgument),
            context.Home,
            context.RunId,
            context.BlockId,
            context.InvocationId,
            cancellationToken
        );
        return 0;
    }
);

return await rootCommand.Parse(args).InvokeAsync();

static async Task<int> RunAsync(string packetPath, bool debug, CancellationToken cancellationToken)
{
    var tandemHome = TandemHomeResolver.Resolve();

    Packet packet;
    try
    {
        packet = new YamlPacketReader().Read(packetPath);
    }
    catch (PacketException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    TandemConfig config;
    try
    {
        config = new TandemConfigurationLoader().Load(tandemHome);
    }
    catch (ConfigurationLoadException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    await using var provider = BuildDeliveryServices(tandemHome, config);
    var chatClients = provider.GetRequiredService<ITandemChatClients>();
    var runPaths = new RunSetup().Create(tandemHome);
    var composition = provider.GetRequiredService<DeliveryComposition>();

    var eventStore = new EventStore(runPaths.RunDirectory);
    var runId = runPaths.RunId.ToString("N");
    await new RunProjectionStore(runPaths.RunDirectory).WriteAsync(
        RunProjection.Initial(
            runPaths.RunId,
            runId,
            DeliveryLifecycleActions.Identity,
            packetPath,
            packet.Repository,
            runPaths.WorkspacePath
        ),
        cancellationToken
    );
    var runProjectors = new Dictionary<string, RunEventProjector>();
    RunEventProjector GetProjector(string blockId)
    {
        if (!runProjectors.TryGetValue(blockId, out var p))
        {
            var profileName = blockId switch
            {
                BlockIds.Planner => "planning",
                BlockIds.Reviewer => "review",
                _ => "implementation",
            };
            p = new RunEventProjector(
                runPaths.RunId,
                blockId,
                eventStore,
                profile: chatClients.ResolveProfile(profileName)
            );
            runProjectors[blockId] = p;
        }
        return p;
    }

    var renderer = new StreamRenderer();
    var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
    var pipeline = composition.Build(
        new Tandem.PipelineBuildContext(
            (blockId, updateRunId, update) =>
            {
                if (updateRunId == runPaths.RunId)
                {
                    if (!interactive)
                    {
                        renderer.RenderUpdate(update);
                    }
                    GetProjector(blockId).EmitAgentUpdateAsync(update).GetAwaiter().GetResult();
                }
            },
            new RunEventBlockExecutionObserver(GetProjector)
        )
    );
    var workflow = Tandem.PipelineMafBridge.GetWorkflow(pipeline);

    var implProfile = chatClients.ResolveProfile("implementation");
    if (!interactive)
    {
        Console.WriteLine($"Run:       {runPaths.RunId}");
        Console.WriteLine($"Workspace: {runPaths.WorkspacePath}");
        Console.WriteLine($"Model:     {implProfile.ProviderName}/{implProfile.Model}");
        Console.WriteLine();
    }

    var initialMessage = new PipelineMessage<DeliveryState>(
        PipelineRuntime.Create(runPaths.RunId),
        DeliveryState.Create(packet, "", runPaths.WorkspacePath)
    );

    await new RunEventProjector(runPaths.RunId, "", eventStore).EmitRunStartedAsync(
        packetPath,
        cancellationToken
    );

    var baseConnectionString =
        Environment.GetEnvironmentVariable("TANDEM_DTS_CONNECTION_STRING")
        ?? "Endpoint=http://localhost:8080;TaskHub=tandem-cli;Authentication=None";

    var connectionString = baseConnectionString;

    try
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                if (!debug)
                {
                    logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
                    logging.AddFilter("Microsoft.DurableTask", LogLevel.Warning);
                    logging.AddFilter("DurableWorkflow", LogLevel.Warning);
                }
            })
            .ConfigureServices(services =>
            {
                services.ConfigureDurableWorkflows(
                    options => options.AddWorkflow(workflow),
                    workerBuilder => workerBuilder.UseDurableTaskScheduler(connectionString),
                    clientBuilder => clientBuilder.UseDurableTaskScheduler(connectionString)
                );
            })
            .Build();

        await host.StartAsync(cancellationToken);

        try
        {
            var workflowClient = host.Services.GetRequiredService<IWorkflowClient>();
            var durableTaskClient = host.Services.GetRequiredService<DurableTaskClient>();
            // Use RunAsync + WaitForCompletionAsync (not StreamAsync, which throws
            // KeyNotFoundException in the pinned durable preview). Live agent updates
            // flow through the side-channel callback wired into the composition,
            // which writes to events.jsonl via RunEventProjector. The dashboard
            // tails events.jsonl and renders from there.
            var run = await workflowClient.RunAsync(
                workflow,
                initialMessage,
                runId,
                cancellationToken
            );

            // Background: wait for completion + emit terminal events + write
            // run.json, so subsequent tandem attach / tandem publish commands can
            // reattach or publish the candidate.
            var completionTask = Task.Run(
                async () =>
                {
                    var final = await ((IAwaitableWorkflowRun)run).WaitForCompletionAsync<
                        PipelineMessage<DeliveryState>
                    >(cancellationToken);
                    renderer.RenderTerminalMessage(final);

                    if (final is not null)
                    {
                        var finalProjector = new RunEventProjector(runPaths.RunId, "", eventStore);
                        switch (final.State.Status)
                        {
                            case Tandem.Domain.RunStatus.Ready:
                                await finalProjector.EmitRunReadyAsync(
                                    final.State.CandidateSha,
                                    cancellationToken
                                );
                                break;
                            case Tandem.Domain.RunStatus.Failed:
                                await finalProjector.EmitRunFailedAsync(
                                    final.LatestOutcome?.Summary ?? "unknown",
                                    cancellationToken
                                );
                                break;
                        }

                        var projection = new RunProjection(
                            runPaths.RunId,
                            runId,
                            DeliveryLifecycleActions.Identity,
                            packetPath,
                            packet.Repository,
                            final.State.Status,
                            ActiveBlockId: null,
                            final.State.PinnedBaseSha,
                            final.State.CandidateSha,
                            runPaths.WorkspacePath,
                            PendingHumanRequest: null,
                            PublishedBranch: null,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow
                        );
                        await new RunProjectionStore(runPaths.RunDirectory).WriteAsync(
                            projection,
                            cancellationToken
                        );
                    }

                    return final;
                },
                cancellationToken
            );

            // Foreground: dashboard loop tails events.jsonl, renders, and
            // handles operator input. When interactive, the operator may
            // detach (q/Ctrl+C), submit answers (Enter), or publish (p) on a
            // Ready run. When non-interactive (piped), the dashboard exits on
            // terminal status.
            var dashboard = new DashboardLoop(
                runPaths.RunDirectory,
                onAnswerSubmitted: answer =>
                    RaiseHumanAnswerAsync(durableTaskClient, runId, answer, cancellationToken),
                onPublishRequested: async () =>
                {
                    var proj = new RunProjectionStore(runPaths.RunDirectory).Read();
                    if (proj is not null)
                    {
                        await PublishCandidateAsync(
                            runPaths.RunDirectory,
                            proj,
                            null,
                            eventStore,
                            cancellationToken
                        );
                    }
                },
                onDetach: () => Task.CompletedTask
            );
            var dashboardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var dashboardTask = dashboard.RunAsync(null, dashboardCts.Token);
            var completedTask = await Task.WhenAny(dashboardTask, completionTask);

            if (completedTask == completionTask && completionTask.IsFaulted)
            {
                dashboardCts.Cancel();
                try
                {
                    await dashboardTask;
                }
                catch (OperationCanceledException)
                {
                    /* close alternate screen before surfacing workflow failure */
                }

                await completionTask;
            }

            // If completion finished first (interactive), wait for the operator
            // to detach. If the dashboard already exited (non-interactive
            // terminal), this returns immediately.
            if (!dashboardTask.IsCompleted)
            {
                await dashboardTask;
            }

            dashboardCts.Cancel();
            try
            {
                if (completionTask.IsCompleted)
                {
                    await completionTask;
                }
            }
            catch (OperationCanceledException)
            {
                /* workflow interrupted by detach */
            }
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
    catch (Exception ex)
    {
        var exitCode = ex switch
        {
            PacketException or ConfigurationLoadException or ProfileResolutionException => 1,
            WorkspacePreparationException => 2,
            _ => 4,
        };
        Console.Error.WriteLine($"Error: {ex.Message}");
        if (debug)
        {
            Console.Error.WriteLine(ex.StackTrace);
            if (ex.InnerException is not null)
            {
                Console.Error.WriteLine($"Inner: {ex.InnerException}");
            }
        }
        return exitCode;
    }

    renderer.Flush();
    renderer.PrintTerminalResult();
    Console.WriteLine();
    Console.WriteLine($"Completed: {runPaths.RunId}");

    var terminalStatus = renderer.TerminalStatus;
    return terminalStatus switch
    {
        Tandem.Domain.RunStatus.Ready => 0,
        Tandem.Domain.RunStatus.Failed => 3,
        Tandem.Domain.RunStatus.WaitingForHuman => 0,
        _ => 4,
    };
}

static async Task<int> AttachAsync(string runIdArg, bool debug, CancellationToken cancellationToken)
{
    var tandemHome = TandemHomeResolver.Resolve();

    TandemConfig config;
    try
    {
        config = new TandemConfigurationLoader().Load(tandemHome);
    }
    catch (ConfigurationLoadException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    var runDir = Path.Combine(tandemHome, "runs", runIdArg);
    if (!Directory.Exists(runDir))
    {
        Console.Error.WriteLine($"Error: Run directory not found: {runDir}");
        return 1;
    }

    var projection = new RunProjectionStore(runDir).Read();
    if (projection is null)
    {
        Console.Error.WriteLine($"Error: run.json not found in {runDir}");
        return 1;
    }

    if (projection.CompositionIdentity != DeliveryLifecycleActions.Identity)
    {
        Console.Error.WriteLine(
            $"Error: Composition '{projection.CompositionIdentity}' is not registered by this host."
        );
        return 1;
    }

    await using var provider = BuildDeliveryServices(tandemHome, config);
    var chatClients = provider.GetRequiredService<ITandemChatClients>();
    var composition = provider.GetRequiredService<DeliveryComposition>();

    var eventStore = new EventStore(runDir);
    var runProjectors = new Dictionary<string, RunEventProjector>();
    RunEventProjector GetProjector(string blockId)
    {
        if (!runProjectors.TryGetValue(blockId, out var projector))
        {
            var profileName = blockId switch
            {
                BlockIds.Planner => "planning",
                BlockIds.Reviewer => "review",
                _ => "implementation",
            };
            projector = new RunEventProjector(
                projection.RunId,
                blockId,
                eventStore,
                profile: chatClients.ResolveProfile(profileName)
            );
            runProjectors[blockId] = projector;
        }
        return projector;
    }

    // Workflow must be registered so MAF recognizes the workflow type
    // when the Durable Task worker reconnects to the existing orchestration.
    var pipeline = composition.Build(
        new Tandem.PipelineBuildContext(
            (blockId, updateRunId, update) =>
            {
                if (updateRunId == projection.RunId)
                {
                    GetProjector(blockId).EmitAgentUpdateAsync(update).GetAwaiter().GetResult();
                }
            },
            new RunEventBlockExecutionObserver(GetProjector)
        )
    );
    var workflow = Tandem.PipelineMafBridge.GetWorkflow(pipeline);

    var baseConnectionString =
        Environment.GetEnvironmentVariable("TANDEM_DTS_CONNECTION_STRING")
        ?? "Endpoint=http://localhost:8080;TaskHub=tandem-cli;Authentication=None";

    try
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                if (!debug)
                {
                    logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
                    logging.AddFilter("Microsoft.DurableTask", LogLevel.Warning);
                    logging.AddFilter("DurableWorkflow", LogLevel.Warning);
                }
            })
            .ConfigureServices(services =>
            {
                services.ConfigureDurableWorkflows(
                    options => options.AddWorkflow(workflow),
                    workerBuilder => workerBuilder.UseDurableTaskScheduler(baseConnectionString),
                    clientBuilder => clientBuilder.UseDurableTaskScheduler(baseConnectionString)
                );
            })
            .Build();

        await host.StartAsync(cancellationToken);
        try
        {
            var durableTaskClient = host.Services.GetRequiredService<DurableTaskClient>();

            var completionTask = Task.Run(
                async () =>
                {
                    var completed = await durableTaskClient.WaitForInstanceCompletionAsync(
                        projection.DurableRunId,
                        getInputsAndOutputs: true,
                        cancellationToken
                    );
                    var final = completed?.ReadOutputAs<PipelineMessage<DeliveryState>>();
                    if (final is not null)
                    {
                        await PersistTerminalProjectionAsync(
                            runDir,
                            projection,
                            final,
                            eventStore,
                            cancellationToken
                        );
                    }
                    return final;
                },
                cancellationToken
            );

            Console.WriteLine($"Attaching to run: {runIdArg}");
            Console.WriteLine($"Durable run ID:   {projection.DurableRunId}");
            Console.WriteLine($"Workspace:        {projection.WorkspacePath}");
            Console.WriteLine();

            var dashboard = new DashboardLoop(
                runDir,
                onAnswerSubmitted: answer =>
                    RaiseHumanAnswerAsync(
                        durableTaskClient,
                        projection.DurableRunId,
                        answer,
                        cancellationToken
                    ),
                onPublishRequested: () =>
                    PublishCandidateAsync(runDir, projection, null, eventStore, cancellationToken),
                onDetach: () => Task.CompletedTask
            );

            var dashboardTask = dashboard.RunAsync(null, cancellationToken);
            var completedTask = await Task.WhenAny(dashboardTask, completionTask);
            if (completedTask == completionTask && completionTask.IsFaulted)
            {
                await completionTask;
            }
            if (!dashboardTask.IsCompleted)
            {
                await dashboardTask;
            }
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        if (debug)
        {
            Console.Error.WriteLine(ex.StackTrace);
        }
        return 4;
    }

    return 0;
}

static ServiceProvider BuildDeliveryServices(string tandemHome, TandemConfig? config)
{
    var services = new ServiceCollection();
    services.AddSingleton(new TandemEnvironment(tandemHome, Environment.ProcessPath));
    if (config is not null)
    {
        services.AddSingleton(config);
    }
    services.AddTandem().AddDelivery();
    return services.BuildServiceProvider();
}

static async Task PersistTerminalProjectionAsync(
    string runDirectory,
    RunProjection projection,
    PipelineMessage<DeliveryState> final,
    EventStore eventStore,
    CancellationToken cancellationToken
)
{
    var projector = new RunEventProjector(projection.RunId, "", eventStore);
    switch (final.State.Status)
    {
        case Tandem.Domain.RunStatus.Ready:
            await projector.EmitRunReadyAsync(final.State.CandidateSha, cancellationToken);
            break;
        case Tandem.Domain.RunStatus.Failed:
            await projector.EmitRunFailedAsync(
                final.LatestOutcome?.Summary ?? "unknown",
                cancellationToken
            );
            break;
    }

    await new RunProjectionStore(runDirectory).WriteAsync(
        projection with
        {
            Status = final.State.Status,
            ActiveBlockId = null,
            PinnedBaseSha = final.State.PinnedBaseSha,
            CandidateSha = final.State.CandidateSha,
            PendingHumanRequest = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        },
        cancellationToken
    );
}

static async Task<int> PublishAsync(
    string runIdArg,
    string? branch,
    bool debug,
    CancellationToken cancellationToken
)
{
    var tandemHome = TandemHomeResolver.Resolve();
    var runDir = Path.Combine(tandemHome, "runs", runIdArg);
    if (!Directory.Exists(runDir))
    {
        Console.Error.WriteLine($"Error: Run directory not found: {runDir}");
        return 1;
    }

    var projection = new RunProjectionStore(runDir).Read();
    if (projection is null)
    {
        Console.Error.WriteLine($"Error: run.json not found in {runDir}");
        return 1;
    }

    var eventStore = new EventStore(runDir);
    return await PublishCandidateAsync(runDir, projection, branch, eventStore, cancellationToken);
}

static async Task<int> PublishCandidateAsync(
    string runDir,
    RunProjection projection,
    string? explicitBranch,
    EventStore eventStore,
    CancellationToken ct
)
{
    if (projection.Status != Tandem.Domain.RunStatus.Ready)
    {
        Console.Error.WriteLine($"Error: Run is not Ready (current: {projection.Status}).");
        return 1;
    }

    if (string.IsNullOrEmpty(projection.CandidateSha))
    {
        Console.Error.WriteLine("Error: No candidate SHA in run projection.");
        return 1;
    }

    if (string.IsNullOrEmpty(projection.RepositoryPath))
    {
        Console.Error.WriteLine("Error: No source repository path in run projection.");
        return 1;
    }

    var candidateSha = projection.CandidateSha!;
    var sourceRepo = projection.RepositoryPath!;
    var workspace = projection.WorkspacePath;

    // Verify workspace HEAD contains the candidate commit.
    var git = new GitProcess();
    var headResult = await git.RunAsync(workspace, ["rev-parse", "HEAD"], ct);
    if (headResult.ExitCode != 0)
    {
        Console.Error.WriteLine($"Error: Could not read workspace HEAD: {headResult.Stderr}");
        return 1;
    }

    var headSha = headResult.Stdout.Trim();
    if (!headSha.StartsWith(candidateSha, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            $"Error: Workspace HEAD ({headSha[..12]}) does not contain candidate ({candidateSha[..12]})."
        );
        return 1;
    }

    // Verify source repo still resolves the pinned base SHA.
    if (!string.IsNullOrEmpty(projection.PinnedBaseSha))
    {
        var baseResult = await git.RunAsync(
            sourceRepo,
            ["cat-file", "-e", projection.PinnedBaseSha!],
            ct
        );
        if (baseResult.ExitCode != 0)
        {
            Console.Error.WriteLine(
                $"Error: Pinned base SHA {projection.PinnedBaseSha} not found in source repo."
            );
            return 1;
        }
    }

    // Derive branch name.
    string branchName;
    if (!string.IsNullOrEmpty(explicitBranch))
    {
        branchName = explicitBranch!;
    }
    else
    {
        var slug = Slugify(Path.GetFileNameWithoutExtension(projection.PacketPath));
        var prefix = runDir[^8..];
        branchName = $"tandem/{slug}-{prefix}";
    }

    // Validate the branch name.
    var validateResult = await git.RunAsync(null, ["check-ref-format", "--branch", branchName], ct);
    if (validateResult.ExitCode != 0)
    {
        Console.Error.WriteLine(
            $"Error: Invalid branch name '{branchName}': {validateResult.Stderr}"
        );
        return 1;
    }

    // Check target branch doesn't already exist.
    var existingResult = await git.RunAsync(
        sourceRepo,
        ["rev-parse", "--verify", $"refs/heads/{branchName}"],
        ct
    );
    if (existingResult.ExitCode == 0)
    {
        Console.Error.WriteLine($"Error: Branch '{branchName}' already exists in source repo.");
        return 1;
    }

    // Record source repo's current state for postcondition.
    var currentBranchResult = await git.RunAsync(
        sourceRepo,
        ["rev-parse", "--abbrev-ref", "HEAD"],
        ct
    );
    var currentBranch = currentBranchResult.Stdout.Trim();
    var sourceStatusBefore = await git.RunAsync(sourceRepo, ["status", "--porcelain"], ct);

    // Transfer the commit: push from workspace to source repo.
    var pushResult = await git.RunAsync(
        workspace,
        ["push", sourceRepo, $"{candidateSha}:refs/heads/{branchName}"],
        ct
    );
    if (pushResult.ExitCode != 0)
    {
        Console.Error.WriteLine($"Error: git push failed: {pushResult.Stderr}");
        return 1;
    }

    // Verify: the branch resolves to the exact candidate SHA.
    var verifyResult = await git.RunAsync(
        sourceRepo,
        ["rev-parse", $"refs/heads/{branchName}"],
        ct
    );
    if (verifyResult.ExitCode != 0)
    {
        Console.Error.WriteLine(
            $"Error: Could not verify branch after push: {verifyResult.Stderr}"
        );
        return 1;
    }

    var publishedSha = verifyResult.Stdout.Trim();
    if (!publishedSha.Equals(candidateSha, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            $"Error: Branch resolves to {publishedSha[..12]} but expected {candidateSha[..12]}."
        );
        return 1;
    }

    // Verify source repo's current branch and working tree are unchanged.
    var currentBranchAfter = await git.RunAsync(
        sourceRepo,
        ["rev-parse", "--abbrev-ref", "HEAD"],
        ct
    );
    if (currentBranchAfter.Stdout.Trim() != currentBranch)
    {
        Console.Error.WriteLine(
            $"Error: Source repo branch changed from '{currentBranch}' to '{currentBranchAfter.Stdout.Trim()}'."
        );
        return 1;
    }

    var sourceStatusAfter = await git.RunAsync(sourceRepo, ["status", "--porcelain"], ct);
    if (sourceStatusAfter.Stdout != sourceStatusBefore.Stdout)
    {
        Console.Error.WriteLine("Error: Source repo working tree changed during publication.");
        return 1;
    }

    // Update run projection.
    var updatedProjection = projection with
    {
        PublishedBranch = branchName,
        Status = Tandem.Domain.RunStatus.Ready,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
    await new RunProjectionStore(runDir).WriteAsync(updatedProjection, ct);

    // Emit run.published event.
    var projector = new RunEventProjector(updatedProjection.RunId, "", eventStore);
    await projector.EmitRunPublishedAsync(branchName, candidateSha, ct);

    Console.WriteLine($"Published: {branchName}");
    Console.WriteLine($"Commit:    {candidateSha}");
    Console.WriteLine($"Repository:{sourceRepo}");
    return 0;
}

static async Task RaiseHumanAnswerAsync(
    DurableTaskClient client,
    string durableRunId,
    string? answerText,
    CancellationToken ct
)
{
    if (string.IsNullOrWhiteSpace(answerText))
    {
        return;
    }

    var answer = new HumanAnswer(answerText.Trim());
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var serialized = JsonSerializer.Serialize(answer, options);
    await client.RaiseEventAsync(durableRunId, "HumanInput", serialized, ct);
}

static string Slugify(string input)
{
    var slug = new StringBuilder();
    var prevDash = false;
    foreach (var c in input.ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(c))
        {
            slug.Append(c);
            prevDash = false;
        }
        else if (!prevDash && slug.Length > 0)
        {
            slug.Append('-');
            prevDash = true;
        }
    }
    if (slug.Length > 0 && slug[^1] == '-')
    {
        slug.Remove(slug.Length - 1, 1);
    }
    return slug.ToString();
}

file sealed class ChatClientBuilderFactory(TandemConfig config)
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
        var client = new InfrastructureChatClientBuilder().Build(profile, apiKey);
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

file sealed class StreamRenderer
{
    private readonly StringBuilder _agent = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<string, string> _toolNames = new();
    private PipelineMessage<DeliveryState>? _finalMessage;

    public Tandem.Domain.RunStatus? TerminalStatus => _finalMessage?.State.Status;

    public Task RenderEvent(WorkflowEvent evt)
    {
        switch (evt)
        {
            case WorkflowOutputEvent outputEvent:
                if (outputEvent.Is<PipelineMessage<DeliveryState>>())
                {
                    var msg = outputEvent.As<PipelineMessage<DeliveryState>>();
                    RenderTerminalBlockTransition(msg);
                }
                break;
        }

        return Task.CompletedTask;
    }

    public void RenderTerminalMessage(PipelineMessage<DeliveryState>? msg)
    {
        if (msg is null)
        {
            return;
        }

        RenderTerminalBlockTransition(msg);
    }

    private void RenderTerminalBlockTransition(PipelineMessage<DeliveryState>? msg)
    {
        if (msg?.LatestOutcome is { } outcome)
        {
            var durStr =
                outcome.Duration.TotalSeconds >= 1
                    ? $"{outcome.Duration.TotalSeconds:F1}s"
                    : $"{outcome.Duration.TotalMilliseconds:F0}ms";
            Console.WriteLine($"[block] {outcome.BlockId} completed: {outcome.Kind} ({durStr})");
        }
        if (
            msg?.State.Status
            is Tandem.Domain.RunStatus.Ready
                or Tandem.Domain.RunStatus.Failed
                or Tandem.Domain.RunStatus.WaitingForHuman
        )
        {
            _finalMessage = msg;
        }
    }

    public void Flush()
    {
        FlushAgent();
        FlushReasoning();
    }

    public void PrintTerminalResult()
    {
        if (_finalMessage is not { } msg)
        {
            return;
        }

        var ctx = msg.State;
        Console.WriteLine();
        Console.WriteLine($"Status:       {ctx.Status}");
        Console.WriteLine($"Run:          {msg.Runtime.RunId}");
        Console.WriteLine($"Base:         {ctx.PinnedBaseSha}");
        if (ctx.CandidateSha is { } candidate)
        {
            Console.WriteLine($"Candidate:    {candidate}");
        }
        Console.WriteLine($"Workspace:    {ctx.WorkspacePath}");
        var passed = ctx.VerificationResults.Count(r => r.ExitCode == 0);
        var total = ctx.VerificationResults.Count;
        if (total > 0)
        {
            Console.WriteLine($"Verification: {passed}/{total}");
        }
        if (ctx.PlannerDecision is { } decision && !string.IsNullOrEmpty(decision.Rationale))
        {
            Console.WriteLine($"Planner:      {decision.Rationale}");
        }
        if (
            ctx.Status == Tandem.Domain.RunStatus.WaitingForHuman
            && ctx.PlannerDecision?.HumanQuestion is { } question
        )
        {
            Console.WriteLine($"Question:     {question}");
        }
    }

    public void RenderUpdate(Tandem.AgentUpdate update)
    {
        var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        switch (update)
        {
            case Tandem.AgentUpdate.Reasoning reasoning:
                _reasoning.Append(reasoning.Value);
                FlushReasoningOnNewline();
                break;
            case Tandem.AgentUpdate.Text text:
                _agent.Append(text.Value);
                FlushAgentOnNewline();
                break;
            case Tandem.AgentUpdate.ToolStarted call:
                FlushAgent();
                FlushReasoning();
                _toolNames[call.CallId] = call.Name;
                Console.WriteLine($"[{ts}] [tool] {call.Name}");
                break;
            case Tandem.AgentUpdate.ToolCompleted result:
                FlushAgent();
                FlushReasoning();
                var name = _toolNames.GetValueOrDefault(result.CallId, result.CallId);
                Console.WriteLine(
                    result.Succeeded
                        ? $"[{ts}] [tool] {name} done"
                        : $"[{ts}] [tool] {name} failed: {result.Error}"
                );
                break;
        }
    }

    private void FlushAgentOnNewline()
    {
        var text = _agent.ToString();
        var nl = text.LastIndexOf('\n');
        if (nl >= 0)
        {
            var line = text[..nl];
            if (!string.IsNullOrWhiteSpace(line))
            {
                Console.WriteLine($"[agent] {line}");
            }
            _agent.Clear();
            _agent.Append(text[(nl + 1)..]);
        }
    }

    private void FlushReasoningOnNewline()
    {
        var text = _reasoning.ToString();
        var nl = text.LastIndexOf('\n');
        if (nl >= 0)
        {
            var line = text[..nl];
            if (!string.IsNullOrWhiteSpace(line))
            {
                Console.WriteLine($"[reasoning] {line}");
            }
            _reasoning.Clear();
            _reasoning.Append(text[(nl + 1)..]);
        }
    }

    private void FlushAgent()
    {
        if (_agent.Length > 0)
        {
            var text = _agent.ToString().TrimEnd();
            if (!string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine($"[agent] {text}");
            }
            _agent.Clear();
        }
    }

    private void FlushReasoning()
    {
        if (_reasoning.Length > 0)
        {
            var text = _reasoning.ToString().TrimEnd();
            if (!string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine($"[reasoning] {text}");
            }
            _reasoning.Clear();
        }
    }
}
