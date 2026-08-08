using System.CommandLine;
using System.Text;
using System.Text.Json;
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
var inspectRunIdArgument = new Argument<string>("run-id") { Description = "Run ID to inspect." };
var acceptedOption = new Option<bool>("--accepted") { Description = "Show accepted values only." };
var stepOption = new Option<string?>("--step") { Description = "Filter by semantic step ID." };
var typeOption = new Option<string?>("--type") { Description = "Filter by value type." };
var jsonOption = new Option<bool>("--json") { Description = "Write machine-readable JSON." };
var inspectCommand = new Command("inspect", "Inspect a persisted run timeline")
{
    inspectRunIdArgument,
    acceptedOption,
    stepOption,
    typeOption,
    jsonOption,
};

var rootCommand = new RootCommand("Tandem — agentic pipeline runner")
{
    runCommand,
    publishCommand,
    inspectCommand,
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

inspectCommand.SetAction(
    async (ParseResult parseResult, CancellationToken cancellationToken) =>
    {
        try
        {
            return await InspectAsync(
                parseResult.GetRequiredValue(inspectRunIdArgument),
                parseResult.GetValue(acceptedOption),
                parseResult.GetValue(stepOption),
                parseResult.GetValue(typeOption),
                parseResult.GetValue(jsonOption),
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
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

    var renderer = new StreamRenderer();
    var journalObserver = new LedgerPipelineObserver(ledgerStore.ForRun(runPaths.RunId));
    var liveTranscript = new LiveTranscript();
    try
    {
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var runObserver = new CompositePipelineObserver(
            journalObserver,
            liveTranscript,
            new TerminalPipelineObserver(renderer, interactive)
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

        await journalObserver.RecordRunStartedAsync(cancellationToken);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var humanInteraction = new TerminalHumanInteraction();
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
            if (!interactive)
            {
                renderer.RenderTerminalMessage(final);
            }
            await PersistTerminalAsync(
                runPaths.RunId,
                final,
                ledgerStore,
                journalObserver,
                CancellationToken.None
            );
            return final;
        }

        var dashboard = new DashboardLoop(
            ledgerStore,
            runPaths.RunId,
            liveTranscript,
            canSubmitAnswer: () => humanInteraction.HasPending(runPaths.RunId),
            onAnswerSubmitted: answer => humanInteraction.SubmitAsync(runPaths.RunId, answer),
            onPublishRequested: async () =>
            {
                await PublishFromLedgerAsync(tandemHome, runPaths.RunId, null, cancellationToken);
            },
            onCancel: () =>
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
            ledgerStore,
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
            ledgerStore,
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
    var recordedRun = await ledgerStore.GetRunAsync(runPaths.RunId, CancellationToken.None);
    var recordedDelivery = new DeliveryLedger(ledgerStore.ForRun(runPaths.RunId));
    renderer.PrintTerminalResult(
        recordedRun,
        await recordedDelivery.ReadPublicationCandidateAsync(CancellationToken.None),
        await ledgerStore
            .ForRun(runPaths.RunId)
            .ReadAsync(DeliveryLedger.VerificationResults, CancellationToken.None)
    );
    Console.WriteLine();
    Console.WriteLine($"Completed: {runPaths.RunId}");

    return recordedRun.Status switch
    {
        LedgerRunStatus.Ready => 0,
        LedgerRunStatus.Failed => 3,
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
    SqliteLedgerStore ledgerStore,
    LedgerPipelineObserver journalObserver,
    CancellationToken cancellationToken
)
{
    var status =
        final.Status == PipelineRunStatus.Succeeded
            ? Tandem.Delivery.RunStatus.Ready
            : Tandem.Delivery.RunStatus.Failed;
    await journalObserver.RecordRunCompletedAsync(status.ToString(), cancellationToken);
    await ledgerStore.CompleteRunAsync(
        runId,
        status == Tandem.Delivery.RunStatus.Ready ? LedgerRunStatus.Ready : LedgerRunStatus.Failed,
        cancellationToken
    );
}

static async Task TryPersistInterruptedRunAsync(
    Guid runId,
    SqliteLedgerStore ledgerStore,
    LedgerPipelineObserver journalObserver,
    Tandem.Delivery.RunStatus status,
    string reason
)
{
    try
    {
        await journalObserver.RecordRunCompletedAsync(status.ToString(), CancellationToken.None);
        await ledgerStore.CompleteRunAsync(
            runId,
            status == Tandem.Delivery.RunStatus.Cancelled
                ? LedgerRunStatus.Cancelled
                : LedgerRunStatus.Faulted,
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

static async Task<int> InspectAsync(
    string runIdArg,
    bool acceptedOnly,
    string? step,
    string? valueType,
    bool json,
    CancellationToken cancellationToken
)
{
    if (!Guid.TryParse(runIdArg, out var runId))
    {
        throw new InvalidOperationException($"Invalid run ID '{runIdArg}'.");
    }
    var tandemHome = TandemHomeResolver.Resolve();
    var store = new SqliteLedgerStore(Path.Combine(tandemHome, "ledger.sqlite3"));
    await store.InitializeAsync(cancellationToken);
    var inspection = await new RunInspector(store).InspectAsync(
        runId,
        acceptedOnly,
        step,
        valueType,
        cancellationToken
    );

    if (json)
    {
        Console.WriteLine(
            JsonSerializer.Serialize(
                inspection,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }
            )
        );
        return 0;
    }

    Console.WriteLine($"Run {inspection.RunId:N}  {inspection.Composition}  {inspection.Status}");
    foreach (var item in inspection.Items)
    {
        var name = item.Name ?? item.ValueType;
        Console.WriteLine(
            $"{item.Timestamp:O}  [{item.Category}] {item.Kind}  {item.StepId}"
                + (string.IsNullOrWhiteSpace(name) ? "" : $"  {name}")
                + (string.IsNullOrWhiteSpace(item.Result) ? "" : $"  {item.Result}")
                + (string.IsNullOrWhiteSpace(item.Identity) ? "" : $"  id={item.Identity}")
                + (
                    string.IsNullOrWhiteSpace(item.OutcomeKind)
                        ? ""
                        : $"  outcome={item.OutcomeKind}"
                )
        );
        if (item.Payload is { } payload)
        {
            var formatted = JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }
            );
            foreach (var line in formatted.Split(Environment.NewLine))
            {
                Console.WriteLine($"    {line}");
            }
        }
    }
    return 0;
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
    var publication = await PublishFromLedgerAsync(tandemHome, runId, branch, cancellationToken);
    Console.WriteLine($"Published: {publication.Branch}");
    Console.WriteLine($"Commit:    {publication.CandidateSha}");
    Console.WriteLine($"Repository:{publication.Repository}");
    return 0;
}

static async Task<PublicationResultRecord> PublishFromLedgerAsync(
    string tandemHome,
    Guid runId,
    string? branch,
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
    await new PublicationOperation(new GitProcess(), records).ExecuteAsync(
        branch,
        cancellationToken
    );
    var publication = (
        await store.ForRun(runId).ReadAsync(DeliveryLedger.PublicationResults, cancellationToken)
    )
        .Last()
        .Value;
    return publication;
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

        RenderTerminalStepTransition(msg);
    }

    private void RenderTerminalStepTransition(PipelineRunResult<DeliveryState>? msg)
    {
        if (msg?.Outcome is { } outcome)
        {
            var durStr =
                outcome.Duration.TotalSeconds >= 1
                    ? $"{outcome.Duration.TotalSeconds:F1}s"
                    : $"{outcome.Duration.TotalMilliseconds:F0}ms";
            Console.WriteLine($"[step] {outcome.StepId} completed: {outcome.Kind} ({durStr})");
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

    public void PrintTerminalResult(
        LedgerRun run,
        PublicationCandidateDocument? candidate,
        IReadOnlyList<AcceptedLedgerEntry<VerificationResultRecord>> verification
    )
    {
        Console.WriteLine();
        Console.WriteLine($"Status:       {run.Status}");
        Console.WriteLine($"Run:          {run.RunId}");
        if (candidate is not null)
        {
            Console.WriteLine($"Base:         {candidate.PinnedBaseSha}");
            Console.WriteLine($"Candidate:    {candidate.CandidateSha}");
            Console.WriteLine($"Workspace:    {candidate.WorkspacePath}");
        }
        var passed = verification.Count(entry => entry.Value.Result.ExitCode == 0);
        var total = verification.Count;
        if (total > 0)
        {
            Console.WriteLine($"Verification: {passed}/{total}");
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

file sealed class TerminalPipelineObserver(StreamRenderer renderer, bool interactive)
    : IPipelineObserver
{
    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        if (!interactive && observation is PipelineAgentUpdated update)
        {
            renderer.RenderUpdate(update.Update);
        }
        return ValueTask.CompletedTask;
    }
}
