using System.CommandLine;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tandem.Application;
using Tandem.Domain;
using Tandem.Infrastructure;
using Tandem.Infrastructure.Composition;
using Tandem.Infrastructure.Lifecycle;
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

var lifecycleCommand = new Command("lifecycle", "Host lifecycle MCP tools over stdio")
{
    Hidden = true,
};

var mcpCommand = new Command("mcp") { lifecycleCommand };

var rootCommand = new RootCommand("Tandem — agentic pipeline runner") { runCommand, mcpCommand };

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

lifecycleCommand.SetAction(
    async (ParseResult parseResult, CancellationToken cancellationToken) =>
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

        await LifecycleMcpHost.RunAsync(
            tandemHome,
            Guid.Parse(runId),
            blockId,
            invocationId,
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

    var chatClientFactory = new ChatClientBuilderFactory(config);
    var runPaths = new RunSetup().Create(tandemHome);

    var composition = new SimpleV1Composition(
        tandemHome,
        chatClientFactory.Build,
        chatClientFactory.ResolveProfile
    );
    var renderer = new StreamRenderer();
    var workflow = composition.Build(
        (updateRunId, update) =>
        {
            if (updateRunId == runPaths.RunId)
            {
                renderer.RenderUpdate(update);
            }
        }
    );

    var implProfile = chatClientFactory.ResolveProfile("implementation");
    Console.WriteLine($"Run:       {runPaths.RunId}");
    Console.WriteLine($"Workspace: {runPaths.WorkspacePath}");
    Console.WriteLine($"Model:     {implProfile.ProviderName}/{implProfile.Model}");
    Console.WriteLine();

    var initialMessage = new PipelineMessage(
        PipelineContext.Create(runPaths.RunId, packet, "", runPaths.WorkspacePath)
    );

    var baseConnectionString =
        Environment.GetEnvironmentVariable("TANDEM_DTS_CONNECTION_STRING")
        ?? "Endpoint=http://localhost:8080;TaskHub=tandem-cli;Authentication=None";

    var runId = runPaths.RunId.ToString("N");
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
            // Use RunAsync instead of StreamAsync. The pinned durable preview
            // (1.16.0-preview.260730.1) throws KeyNotFoundException in
            // DeserializeEventByType for routed WorkflowOutputEvent shapes
            // during WatchStreamAsync. Live model streaming still flows through
            // the side-channel callback wired into the composition; only
            // intermediate block transitions are deferred to completion.
            var run = await workflowClient.RunAsync(
                workflow,
                initialMessage,
                runId,
                cancellationToken
            );

            var finalMessage = await (
                (IAwaitableWorkflowRun)run
            ).WaitForCompletionAsync<PipelineMessage>(cancellationToken);
            renderer.RenderTerminalMessage(finalMessage);
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
    private PipelineMessage? _finalMessage;

    public Tandem.Domain.RunStatus? TerminalStatus => _finalMessage?.Context.Status;

    public Task RenderEvent(WorkflowEvent evt)
    {
        switch (evt)
        {
            case AgentResponseUpdateEvent updateEvent:
                RenderUpdate(updateEvent.Update);
                break;
            case WorkflowOutputEvent outputEvent:
                if (outputEvent.Is<PipelineMessage>())
                {
                    var msg = outputEvent.As<PipelineMessage>();
                    RenderTerminalBlockTransition(msg);
                }
                break;
        }

        return Task.CompletedTask;
    }

    public void RenderTerminalMessage(PipelineMessage? msg)
    {
        if (msg is null)
        {
            return;
        }

        RenderTerminalBlockTransition(msg);
    }

    private void RenderTerminalBlockTransition(PipelineMessage? msg)
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
            msg?.Context.Status
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

        var ctx = msg.Context;
        Console.WriteLine();
        Console.WriteLine($"Status:       {ctx.Status}");
        Console.WriteLine($"Run:          {ctx.RunId}");
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
            Console.WriteLine($"Review:       {decision.Rationale}");
        }
        if (
            ctx.Status == Tandem.Domain.RunStatus.WaitingForHuman
            && ctx.PlannerDecision?.HumanQuestion is { } question
        )
        {
            Console.WriteLine($"Question:     {question}");
        }
    }

    public void RenderUpdate(AgentResponseUpdate update)
    {
        var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning:
                    _reasoning.Append(reasoning.Text);
                    FlushReasoningOnNewline();
                    break;
                case TextContent text:
                    _agent.Append(text.Text);
                    FlushAgentOnNewline();
                    break;
                case FunctionCallContent call:
                    FlushAgent();
                    FlushReasoning();
                    _toolNames[call.CallId] = call.Name;
                    Console.WriteLine($"[{ts}] [tool] {call.Name}");
                    break;
                case FunctionResultContent result:
                    FlushAgent();
                    FlushReasoning();
                    var name = _toolNames.GetValueOrDefault(result.CallId, result.CallId);
                    if (result.Exception is not null)
                    {
                        Console.WriteLine(
                            $"[{ts}] [tool] {name} failed: {result.Exception.Message}"
                        );
                    }
                    else
                    {
                        Console.WriteLine($"[{ts}] [tool] {name} done");
                    }
                    break;
            }
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
