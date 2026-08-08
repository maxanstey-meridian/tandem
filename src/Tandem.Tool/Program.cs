using System.CommandLine;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem;
using Tandem.Application;
using Tandem.Delivery;
using Tandem.Domain;
using Tandem.Git;
using Tandem.Infrastructure;
using Tandem.Infrastructure.Dashboard;
using Tandem.Infrastructure.Projection;
using Tandem.Interfaces;
using Tandem.Tool;
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

var rootCommand = new RootCommand("Tandem — agentic pipeline runner")
{
    runCommand,
    publishCommand,
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

    await using var provider = BuildDeliveryServices(config);
    var chatClients = provider.GetRequiredService<TandemChatClients>();
    var runPaths = new RunSetup().Create(tandemHome);
    var composition = provider.GetRequiredService<DeliveryComposition>();

    var eventStore = new EventStore(runPaths.RunDirectory);
    var projection = RunProjection.Initial(
        runPaths.RunId,
        "delivery",
        packetPath,
        packet.Repository,
        runPaths.WorkspacePath
    );
    await new RunProjectionStore(runPaths.RunDirectory).WriteAsync(projection, cancellationToken);
    var renderer = new StreamRenderer();
    try
    {
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

        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var runObserver = new RunEventPipelineObserver(
            GetProjector,
            update =>
            {
                if (!interactive)
                {
                    renderer.RenderUpdate(update);
                }
            }
        );
        var pipeline = composition.Build();

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

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var humanInteraction = new TerminalHumanInteraction();
        var interactions = new PipelineInteractionHandlers().Handle<HumanQuestion, HumanAnswer>(
            humanInteraction.WaitAsync
        );
        var runner = new PipelineRunner();
        var completionTask = CompleteRunAsync();

        async Task<PipelineRunResult<DeliveryState>> CompleteRunAsync()
        {
            var final = await runner.RunAsync(
                pipeline,
                DeliveryState.Create(packet, "", runPaths.WorkspacePath),
                new PipelineRunOptions(runPaths.RunId, interactions, runObserver),
                runCts.Token
            );
            renderer.RenderTerminalMessage(final);
            await PersistTerminalProjectionAsync(
                runPaths.RunDirectory,
                projection,
                final,
                eventStore,
                CancellationToken.None
            );
            return final;
        }

        var dashboard = new DashboardLoop(
            runPaths.RunDirectory,
            onAnswerSubmitted: answer => humanInteraction.SubmitAsync(runPaths.RunId, answer),
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

        if (firstCompleted == dashboardTask && dashboardTask.IsFaulted)
        {
            runCts.Cancel();
            try
            {
                await completionTask;
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested) { }
            await dashboardTask;
        }

        if (firstCompleted == dashboardTask && !completionTask.IsCompleted)
        {
            runCts.Cancel();
        }

        await completionTask;

        await dashboardTask;
    }
    catch (OperationCanceledException)
    {
        await TryPersistInterruptedRunAsync(
            runPaths.RunDirectory,
            projection,
            eventStore,
            Tandem.Delivery.RunStatus.Cancelled,
            "Run cancelled."
        );
        Console.Error.WriteLine("Cancelled.");
        return 4;
    }
    catch (Exception ex)
    {
        var reportedException = ex is PipelineRunException { InnerException: { } innerException }
            ? innerException
            : ex;
        await TryPersistInterruptedRunAsync(
            runPaths.RunDirectory,
            projection,
            eventStore,
            Tandem.Delivery.RunStatus.Faulted,
            reportedException.Message
        );
        var exitCode = reportedException switch
        {
            PacketException or ConfigurationLoadException or ProfileResolutionException => 1,
            WorkspacePreparationException => 2,
            _ => 4,
        };
        Console.Error.WriteLine($"Error: {reportedException.Message}");
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
        Tandem.Delivery.RunStatus.Ready => 0,
        Tandem.Delivery.RunStatus.Failed => 3,
        Tandem.Delivery.RunStatus.WaitingForHuman => 0,
        _ => 4,
    };
}

static ServiceProvider BuildDeliveryServices(TandemConfig config)
{
    var services = new ServiceCollection();
    var clients = new TandemChatClients(config);
    services.AddSingleton(config);
    services.AddSingleton(clients);
    services
        .AddTandem()
        .AddDelivery(
            new DeliveryOptions(
                clients.Build,
                name =>
                {
                    var profile = clients.ResolveProfile(name);
                    return new DeliveryAgentProfile(
                        profile.ContextWindowTokens,
                        profile.MaxOutputTokens,
                        profile.CheckpointAtPercent
                    );
                }
            )
        );
    return services.BuildServiceProvider();
}

static async Task PersistTerminalProjectionAsync(
    string runDirectory,
    RunProjection projection,
    PipelineRunResult<DeliveryState> final,
    EventStore eventStore,
    CancellationToken cancellationToken
)
{
    var projector = new RunEventProjector(projection.RunId, "", eventStore);
    switch (final.State.Status)
    {
        case Tandem.Delivery.RunStatus.Ready:
            await projector.EmitRunReadyAsync(final.State.CandidateSha, cancellationToken);
            break;
        case Tandem.Delivery.RunStatus.Failed:
            await projector.EmitRunFailedAsync(
                final.Outcome?.Summary ?? "unknown",
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

static async Task TryPersistInterruptedRunAsync(
    string runDirectory,
    RunProjection projection,
    EventStore eventStore,
    Tandem.Delivery.RunStatus status,
    string reason
)
{
    try
    {
        var projector = new RunEventProjector(projection.RunId, "", eventStore);
        if (status == Tandem.Delivery.RunStatus.Cancelled)
        {
            await projector.EmitRunCancelledAsync(reason, CancellationToken.None);
        }
        else
        {
            await projector.EmitRunFaultedAsync(reason, CancellationToken.None);
        }
        await new RunProjectionStore(runDirectory).WriteAsync(
            projection with
            {
                Status = status,
                ActiveBlockId = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );
    }
    catch (Exception persistenceError)
    {
        Console.Error.WriteLine(
            $"Warning: failed to persist terminal run state: {persistenceError.Message}"
        );
    }
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
    if (projection.Status != Tandem.Delivery.RunStatus.Ready)
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
        Status = Tandem.Delivery.RunStatus.Ready,
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

file sealed class TerminalHumanInteraction
{
    private readonly object _sync = new();
    private Pending? _pending;

    public async ValueTask<HumanAnswer> WaitAsync(
        PipelineInteractionContext<HumanQuestion, HumanAnswer> context,
        CancellationToken cancellationToken
    )
    {
        var pending = new Pending(context);
        lock (_sync)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("A human interaction is already pending.");
            }
            _pending = pending;
        }

        try
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    public Task SubmitAsync(Guid runId, string? answerText)
    {
        if (string.IsNullOrWhiteSpace(answerText))
        {
            return Task.CompletedTask;
        }

        Pending pending;
        lock (_sync)
        {
            pending =
                _pending
                ?? throw new InvalidOperationException(
                    $"Run '{runId:N}' has no pending human interaction."
                );
            if (pending.Context.RunId != runId)
            {
                throw new InvalidOperationException(
                    $"Pending interaction belongs to run '{pending.Context.RunId:N}', not '{runId:N}'."
                );
            }
            _pending = null;
        }

        if (!pending.Completion.TrySetResult(new HumanAnswer(answerText.Trim())))
        {
            throw new InvalidOperationException(
                $"Interaction '{pending.Context.InteractionId}' no longer accepts answers."
            );
        }
        return Task.CompletedTask;
    }

    private sealed class Pending(PipelineInteractionContext<HumanQuestion, HumanAnswer> context)
    {
        public PipelineInteractionContext<HumanQuestion, HumanAnswer> Context { get; } = context;
        public TaskCompletionSource<HumanAnswer> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

file sealed class StreamRenderer
{
    private readonly StringBuilder _agent = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<string, string> _toolNames = new();
    private PipelineRunResult<DeliveryState>? _finalMessage;

    public Tandem.Delivery.RunStatus? TerminalStatus => _finalMessage?.State.Status;

    public void RenderTerminalMessage(PipelineRunResult<DeliveryState>? msg)
    {
        if (msg is null)
        {
            return;
        }

        RenderTerminalBlockTransition(msg);
    }

    private void RenderTerminalBlockTransition(PipelineRunResult<DeliveryState>? msg)
    {
        if (msg?.Outcome is { } outcome)
        {
            var durStr =
                outcome.Duration.TotalSeconds >= 1
                    ? $"{outcome.Duration.TotalSeconds:F1}s"
                    : $"{outcome.Duration.TotalMilliseconds:F0}ms";
            Console.WriteLine($"[block] {outcome.StepId} completed: {outcome.Kind} ({durStr})");
        }
        if (
            msg?.State.Status
            is Tandem.Delivery.RunStatus.Ready
                or Tandem.Delivery.RunStatus.Failed
                or Tandem.Delivery.RunStatus.WaitingForHuman
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
        Console.WriteLine($"Run:          {msg.RunId}");
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
            ctx.Status == Tandem.Delivery.RunStatus.WaitingForHuman
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
