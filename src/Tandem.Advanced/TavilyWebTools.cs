using Microsoft.Extensions.AI;
using Tandem.Infrastructure;
using Tavily;

namespace Tandem.Advanced;

internal static class TavilyWebTools
{
    internal const string ApiKeyEnvironmentVariable = "TAVILY_API_KEY";
    internal const string SearchName = "web_search";
    internal const string FetchName = "web_fetch";

    internal static void Add(
        AgentImplementationContext context,
        Func<string, bool, bool, (AIFunction? Search, AIFunction? Fetch)>? createTools = null,
        Func<string, string?>? getEnvironmentVariable = null
    )
    {
        var workspace = context.Workspace;
        if (workspace is null || (!workspace.IncludeWebSearch && !workspace.IncludeWebFetch))
        {
            return;
        }

        var apiKey = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(
            ApiKeyEnvironmentVariable
        );
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Agent '{context.Id}' selected a Tavily web tool, but {ApiKeyEnvironmentVariable} is missing or blank. Set {ApiKeyEnvironmentVariable} before running this agent."
            );
        }

        var created = (createTools ?? CreateTools)(
            apiKey,
            workspace.IncludeWebSearch,
            workspace.IncludeWebFetch
        );
        if (workspace.IncludeWebSearch)
        {
            Add(new RenamedAIFunction(created.Search!, SearchName));
        }
        if (workspace.IncludeWebFetch)
        {
            Add(new RenamedAIFunction(created.Fetch!, FetchName));
        }

        void Add(AIFunction tool) =>
            HarnessTools.Add(
                context.ChatOptions,
                context.ToolEffects,
                tool,
                Infrastructure.ToolEffect.Read
            );
    }

    private static (AIFunction? Search, AIFunction? Fetch) CreateTools(
        string apiKey,
        bool includeSearch,
        bool includeFetch
    )
    {
        var client = new TavilyClient(apiKey);
        return (
            includeSearch ? client.AsSearchTool() : null,
            includeFetch ? client.AsExtractTool() : null
        );
    }

    private sealed class RenamedAIFunction(AIFunction inner, string name)
        : DelegatingAIFunction(inner)
    {
        public override string Name => name;
    }
}
