using System.CommandLine;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem;
using Tandem.Advanced;
using Tandem.Application;
using Tandem.Delivery;
using Tandem.Domain;
using Tandem.Git;
using Tandem.Infrastructure;
using Tandem.Infrastructure.Dashboard;
using Tandem.Infrastructure.Projection;
using Tandem.Interfaces;
using Tandem.Ledger;
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

    var runPaths = new RunSetup().Create(tandemHome);
    var ledgerStore = new SqliteLedgerStore(Path.Combine(tandemHome, "ledger.sqlite3"));
    await ledgerStore.InitializeAsync(cancellationToken);
    await ledgerStore.CreateRunAsync(runPaths.RunId, "delivery", cancellationToken);
    var deliveryLedger = new DeliveryLedger(ledgerStore.ForRun(runPaths.RunId));
    await deliveryLedger.InitializeAsync(packet, cancellationToken);
    await using var provider = BuildDeliveryServices(config, deliveryLedger);
    var chatClients = provider.GetRequiredService<TandemChatClients>();
    var composition = provider.GetRequiredService<DeliveryComposition>();

    var eventStore = new EventStore(runPaths.RunDirectory);
    var renderer = new StreamRenderer();
    var journalObserver = new LedgerPipelineObserver(ledgerStore.ForRun(runPaths.RunId));
    try
    {
        var runProjectors = new Dictionary<string, RunEventProjector>();
        RunEventProjector GetProjector(string blockId)
        {
            if (!runProjectors.TryGetValue(blockId, out var p))
            {
                var profileName = blockId switch
                {
                    DeliveryIds.Planner => "planning",
                    DeliveryIds.Reviewer => "review",
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
        var dashboardObserver = new RunEventPipelineObserver(
            GetProjector,
            update =>
            {
                if (!interactive)
                {
                    renderer.RenderUpdate(update);
                }
            }
        );
        var runObserver = new CompositePipelineObserver(journalObserver, dashboardObserver);
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
        await journalObserver.RecordRunStartedAsync(cancellationToken);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var humanInteraction = new TerminalHumanInteraction(deliveryLedger);
        var interactions = new PipelineInteractionHandlers()
            .Handle(composition.PlannerHumanInput, humanInteraction.WaitAsync)
            .Handle(composition.ReviewerHumanInput, humanInteraction.WaitAsync);
        var runner = new PipelineRunner();
        var completionTask = CompleteRunAsync();

        async Task<PipelineRunResult<DeliveryState>> CompleteRunAsync()
        {
            var final = await runner.RunAsync(
                pipeline,
                DeliveryState.Create(packet, "", runPaths.WorkspacePath),
                new PipelineRunOptions(
                    runPaths.RunId,
                    interactions,
                    runObserver
                ).WithAcceptanceUnitOfWork(new LedgerUnitOfWork(ledgerStore)),
                runCts.Token
            );
            renderer.RenderTerminalMessage(final);
            await PersistTerminalAsync(
                runPaths.RunId,
                final,
                eventStore,
                ledgerStore,
                deliveryLedger,
                journalObserver,
                CancellationToken.None
            );
            return final;
        }

        var dashboard = new DashboardLoop(
            runPaths.RunDirectory,
            onAnswerSubmitted: answer => humanInteraction.SubmitAsync(runPaths.RunId, answer),
            onPublishRequested: async () =>
            {
                await PublishFromLedgerAsync(
                    tandemHome,
                    runPaths.RunId,
                    null,
                    eventStore,
                    cancellationToken
                );
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
            runPaths.RunId,
            eventStore,
            ledgerStore,
            deliveryLedger,
            journalObserver,
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
            runPaths.RunId,
            eventStore,
            ledgerStore,
            deliveryLedger,
            journalObserver,
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

static ServiceProvider BuildDeliveryServices(TandemConfig config, IDeliveryRecordSink records)
{
    var services = new ServiceCollection();
    var clients = new TandemChatClients(config);
    services.AddSingleton(config);
    services.AddSingleton(clients);
    services.AddDelivery(
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
            },
            records
        )
    );
    return services.BuildServiceProvider();
}

static async Task PersistTerminalAsync(
    Guid runId,
    PipelineRunResult<DeliveryState> final,
    EventStore eventStore,
    SqliteLedgerStore ledgerStore,
    DeliveryLedger deliveryLedger,
    LedgerPipelineObserver journalObserver,
    CancellationToken cancellationToken
)
{
    var projector = new RunEventProjector(runId, "", eventStore);
    var status =
        final.Status == PipelineRunStatus.Succeeded
            ? Tandem.Delivery.RunStatus.Ready
            : Tandem.Delivery.RunStatus.Failed;
    await deliveryLedger.AcceptTerminalOutcomeAsync(
        $"terminal--{status}",
        new TerminalOutcomeRecord(
            status.ToString(),
            final.State.CandidateSha,
            final.Outcome?.Summary
        ),
        cancellationToken
    );
    await journalObserver.RecordRunCompletedAsync(status.ToString(), cancellationToken);
    await ledgerStore.CompleteRunAsync(
        runId,
        status == Tandem.Delivery.RunStatus.Ready ? LedgerRunStatus.Ready : LedgerRunStatus.Failed,
        cancellationToken
    );
    switch (status)
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
}

static async Task TryPersistInterruptedRunAsync(
    Guid runId,
    EventStore eventStore,
    SqliteLedgerStore ledgerStore,
    DeliveryLedger deliveryLedger,
    LedgerPipelineObserver journalObserver,
    Tandem.Delivery.RunStatus status,
    string reason
)
{
    try
    {
        await deliveryLedger.AcceptTerminalOutcomeAsync(
            $"terminal--{status}",
            new TerminalOutcomeRecord(status.ToString(), null, reason),
            CancellationToken.None
        );
        await journalObserver.RecordRunCompletedAsync(status.ToString(), CancellationToken.None);
        await ledgerStore.CompleteRunAsync(
            runId,
            status == Tandem.Delivery.RunStatus.Cancelled
                ? LedgerRunStatus.Cancelled
                : LedgerRunStatus.Faulted,
            CancellationToken.None
        );
        var projector = new RunEventProjector(runId, "", eventStore);
        if (status == Tandem.Delivery.RunStatus.Cancelled)
        {
            await projector.EmitRunCancelledAsync(reason, CancellationToken.None);
        }
        else
        {
            await projector.EmitRunFaultedAsync(reason, CancellationToken.None);
        }
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
    if (!Guid.TryParse(runIdArg, out var runId))
    {
        Console.Error.WriteLine($"Error: Invalid run ID '{runIdArg}'.");
        return 1;
    }
    var runDir = Path.Combine(tandemHome, "runs", runId.ToString("N"));
    var eventStore = new EventStore(runDir);
    return await PublishFromLedgerAsync(tandemHome, runId, branch, eventStore, cancellationToken);
}

static async Task<int> PublishFromLedgerAsync(
    string tandemHome,
    Guid runId,
    string? branch,
    EventStore eventStore,
    CancellationToken cancellationToken
)
{
    var store = new SqliteLedgerStore(Path.Combine(tandemHome, "ledger.sqlite3"));
    await store.InitializeAsync(cancellationToken);
    var run = await store.GetRunAsync(runId, cancellationToken);
    if (run.Status != LedgerRunStatus.Ready)
    {
        throw new InvalidOperationException($"Run is not Ready (current: {run.Status}).");
    }
    var records = new DeliveryLedger(store.ForRun(runId));
    var result = await new PublicationOperation(new GitProcess(), records).ExecuteAsync(
        branch,
        cancellationToken
    );
    await new RunEventProjector(runId, "", eventStore).EmitRunPublishedAsync(
        result.Branch,
        result.CandidateSha,
        cancellationToken
    );
    Console.WriteLine($"Published: {result.Branch}");
    Console.WriteLine($"Commit:    {result.CandidateSha}");
    Console.WriteLine($"Repository:{result.Repository}");
    return 0;
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
    private PipelineRunResult<DeliveryState>? _finalMessage;

    public Tandem.Delivery.RunStatus? TerminalStatus =>
        _finalMessage is null ? null
        : _finalMessage.Status == PipelineRunStatus.Succeeded ? Tandem.Delivery.RunStatus.Ready
        : Tandem.Delivery.RunStatus.Failed;

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
        if (msg is not null)
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
        Console.WriteLine($"Status:       {TerminalStatus}");
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
        var question = ctx.PlannerDecision?.HumanQuestion ?? ctx.ReviewerDecision?.HumanQuestion;
        if (question is not null)
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
