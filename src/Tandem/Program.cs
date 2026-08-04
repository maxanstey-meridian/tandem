using System.CommandLine;
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

var rootCommand = new RootCommand("Tandem — agentic pipeline runner")
{
    packetArgument,
    debugOption,
};

rootCommand.SetAction(
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

    BlockResult? result = null;
    try
    {
        var runner = new WorkflowRunner();
        result = await runner.RunAsync(runContext, apiKey, RenderEvent, cancellationToken);
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

    Console.WriteLine();
    Console.WriteLine($"Completed: {runPaths.RunId}");
    Console.WriteLine($"Workspace: {runPaths.WorkspacePath}");
    Console.WriteLine($"Result:    {result.FinalResponse}");

    return 0;
}

static Task RenderEvent(WorkflowEvent evt)
{
    if (evt is AgentResponseUpdateEvent updateEvent)
    {
        RenderUpdate(updateEvent.Update);
    }

    return Task.CompletedTask;
}

static void RenderUpdate(AgentResponseUpdate update)
{
    foreach (var content in update.Contents)
    {
        switch (content)
        {
            case TextReasoningContent reasoning:
                Console.WriteLine($"[reasoning] {reasoning.Text}");
                break;
            case TextContent text:
                Console.WriteLine($"[agent] {text.Text}");
                break;
            case FunctionCallContent call:
                Console.WriteLine($"[tool] {call.Name}");
                break;
            case FunctionResultContent result:
                if (result.Exception is not null)
                {
                    Console.WriteLine($"[tool] {result.CallId} failed: {result.Exception.Message}");
                }
                else
                {
                    Console.WriteLine($"[tool] {result.CallId} done");
                }
                break;
        }
    }
}
