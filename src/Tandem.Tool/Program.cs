using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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

var runIdArgument = new Argument<string>("run-id") { Description = "Completed run ID." };

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
    var projection = RunProjection.Initial(
        runPaths.RunId,
        DeliveryLifecycleActions.Identity,
        packetPath,
        packet.Repository,
        runPaths.WorkspacePath
    );
    await new RunProjectionStore(runPaths.RunDirectory).WriteAsync(projection, cancellationToken);
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
    using var agentUpdates = Tandem.AgentUpdates.Observe(
        runPaths.RunId,
        (blockId, _, update) =>
        {
            if (!interactive)
            {
                renderer.RenderUpdate(update);
            }
            GetProjector(blockId).EmitAgentUpdateAsync(update).GetAwaiter().GetResult();
        }
    );
    var pipeline = composition.Build(
        new Tandem.PipelineBuildContext(
            ExecutionObserver: new RunEventBlockExecutionObserver(GetProjector)
        )
    );

    var implProfile = chatClients.ResolveProfile("implementation");
    if (!interactive)
    {
        Console.WriteLine($"Run:       {runPaths.RunId}");
        Console.WriteLine($"Workspace: {runPaths.WorkspacePath}");
        Console.WriteLine($"Model:     {implProfile.ProviderName}/{implProfile.Model}");
        Console.WriteLine();
    }

    await new RunEventProjector(runPaths.RunId, "", eventStore).EmitRunStartedAsync(
        packetPath,
        cancellationToken
    );

    try
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var requests = new InMemoryExternalRequestBroker(
            async (request, requestCancellationToken) =>
            {
                if (
                    request.RequestType != typeof(HumanQuestion).FullName
                    || request.ResponseType != typeof(HumanAnswer).FullName
                )
                {
                    throw new InvalidOperationException(
                        $"Terminal host cannot answer interaction '{request.PortId}' with "
                            + $"request/response types '{request.RequestType}' and "
                            + $"'{request.ResponseType}'."
                    );
                }

                var question =
                    request.Payload.Deserialize<HumanQuestion>()
                    ?? throw new InvalidOperationException(
                        $"Interaction '{request.PortId}' produced an invalid human question."
                    );
                await GetProjector(request.PortId)
                    .EmitHumanRequestedAsync(question, requestCancellationToken);
            }
        );
        var runner = new InProcessPipelineRunner();
        var completionTask = CompleteRunAsync();

        async Task<PipelineMessage<DeliveryState>> CompleteRunAsync()
        {
            var final = await runner.RunAsync(
                pipeline,
                runPaths.RunId,
                DeliveryState.Create(packet, "", runPaths.WorkspacePath),
                requests,
                runCts.Token
            );
            renderer.RenderTerminalMessage(final);
            await PersistTerminalProjectionAsync(
                runPaths.RunDirectory,
                projection,
                final,
                eventStore,
                cancellationToken
            );
            return final;
        }

        var dashboard = new DashboardLoop(
            runPaths.RunDirectory,
            onAnswerSubmitted: answer => SubmitHumanAnswerAsync(requests, runPaths.RunId, answer),
            onPublishRequested: async () =>
            {
                var current = new RunProjectionStore(runPaths.RunDirectory).Read();
                if (current is not null)
                {
                    await PublishCandidateAsync(
                        runPaths.RunDirectory,
                        current,
                        null,
                        eventStore,
                        cancellationToken
                    );
                }
            },
            onDetach: () =>
            {
                if (!completionTask.IsCompleted)
                {
                    runCts.Cancel();
                }
                return Task.CompletedTask;
            }
        );
        using var dashboardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dashboardTask = Task.Run(
            () => dashboard.RunAsync(null, dashboardCts.Token),
            CancellationToken.None
        );
        var firstCompleted = await Task.WhenAny(dashboardTask, completionTask);

        if (firstCompleted == completionTask && completionTask.IsFaulted)
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

        if (firstCompleted == dashboardTask && !completionTask.IsCompleted)
        {
            runCts.Cancel();
        }

        try
        {
            await completionTask;
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested) { }

        await dashboardTask;
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

static Task SubmitHumanAnswerAsync(
    InMemoryExternalRequestBroker requests,
    Guid runId,
    string? answerText
)
{
    if (string.IsNullOrWhiteSpace(answerText))
    {
        return Task.CompletedTask;
    }

    var pending = requests.PendingRequests.Where(request => request.RunId == runId).ToArray();
    if (pending.Length != 1)
    {
        throw new InvalidOperationException(
            $"Expected one pending human request for run '{runId:N}', found {pending.Length}."
        );
    }
    if (pending[0].ResponseType != typeof(HumanAnswer).FullName)
    {
        throw new InvalidOperationException(
            $"Pending interaction '{pending[0].PortId}' does not accept a human answer."
        );
    }

    requests.Answer(
        new ExternalRequestAnswer(
            runId,
            pending[0].RequestId,
            JsonSerializer.SerializeToElement(new HumanAnswer(answerText.Trim()))
        )
    );
    return Task.CompletedTask;
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
