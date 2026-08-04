using System.CommandLine;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Application;
using Tandem.Domain;
using Tandem.Infrastructure;
using Tandem.Interfaces;

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

var rootCommand = new RootCommand("Tandem — agentic pipeline runner") { runCommand };

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

    ResolvedProfile profile;
    string apiKey;
    try
    {
        if (!config.Profiles.TryGetValue("implementation", out var profileConfig))
        {
            throw new ProfileResolutionException("Profile 'implementation' is not configured.");
        }

        if (!config.Providers.TryGetValue(profileConfig.Provider, out var providerConfig))
        {
            throw new ProfileResolutionException(
                $"Provider '{profileConfig.Provider}' referenced by profile 'implementation' is not configured."
            );
        }

        apiKey = EnvironmentApiKeyReader.Read(providerConfig.ApiKeyEnvironmentVariable);
        profile = new ProfileResolver().Resolve(config, "implementation", apiKey);
    }
    catch (ProfileResolutionException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }

    var runPaths = new RunSetup().Create(tandemHome);

    WorkspacePreparationResult prep;
    try
    {
        var prepService = new WorkspacePreparation();
        prep = await prepService.PrepareAsync(
            packet,
            runPaths.RunDirectory,
            runPaths.WorkspacePath,
            cancellationToken
        );
    }
    catch (WorkspacePreparationException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 2;
    }

    var runContext = new RunContext(
        runPaths.RunId,
        packet,
        prep.PinnedBaseSha,
        runPaths.WorkspacePath,
        profile
    );

    Console.WriteLine($"Run:       {runPaths.RunId}");
    Console.WriteLine($"Base:      {prep.PinnedBaseSha}");
    Console.WriteLine($"Workspace: {runPaths.WorkspacePath}");
    Console.WriteLine($"Model:     {profile.ProviderName}/{profile.Model}");
    Console.WriteLine();

    var renderer = new StreamRenderer();
    BlockResult? result = null;
    try
    {
        var runner = new WorkflowRunner();
        result = await runner.RunAsync(runContext, apiKey, renderer.RenderEvent, cancellationToken);
    }
    catch (WorkflowRunException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        if (debug && ex.InnerException is not null)
        {
            Console.Error.WriteLine(ex.InnerException.StackTrace);
        }
        return ex.InnerException is null ? 4 : 3;
    }

    renderer.Flush();
    Console.WriteLine();
    Console.WriteLine($"Completed: {runPaths.RunId}");
    Console.WriteLine($"Workspace: {runPaths.WorkspacePath}");
    Console.WriteLine($"Result:    {result.FinalResponse}");

    return 0;
}

file sealed class StreamRenderer
{
    private readonly StringBuilder _agent = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<string, string> _toolNames = new();

    public Task RenderEvent(WorkflowEvent evt)
    {
        if (evt is AgentResponseUpdateEvent updateEvent)
        {
            RenderUpdate(updateEvent.Update);
        }

        return Task.CompletedTask;
    }

    public void Flush()
    {
        FlushAgent();
        FlushReasoning();
    }

    private void RenderUpdate(AgentResponseUpdate update)
    {
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
                    Console.WriteLine($"[tool] {call.Name}");
                    break;
                case FunctionResultContent result:
                    FlushAgent();
                    FlushReasoning();
                    var name = _toolNames.GetValueOrDefault(result.CallId, result.CallId);
                    if (result.Exception is not null)
                    {
                        Console.WriteLine($"[tool] {name} failed: {result.Exception.Message}");
                    }
                    else
                    {
                        Console.WriteLine($"[tool] {name} done");
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
