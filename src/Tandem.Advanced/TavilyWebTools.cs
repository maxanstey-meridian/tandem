using Microsoft.Extensions.AI;
using Tandem.Infrastructure;
using Tavily;

namespace Tandem.Advanced;

internal static class TavilyWebTools
{
    internal const string ApiKeyEnvironmentVariable = "TAVILY_API_KEY";
    internal const string SearchName = "web_search";
    internal const string FetchName = "web_fetch";

    internal static void Add(AgentImplementationContext context, string? apiKey)
    {
        var workspace = context.Workspace;
        if (workspace is null || (!workspace.IncludeWebSearch && !workspace.IncludeWebFetch))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Agent '{context.Id}' selected a Tavily web tool, but {ApiKeyEnvironmentVariable} is missing or blank. Set {ApiKeyEnvironmentVariable} before running this agent."
            );
        }

        var client = new TavilyClient(apiKey);
        if (workspace.IncludeWebSearch)
        {
            Add(new RenamedAIFunction(client.AsSearchTool(), SearchName));
        }
        if (workspace.IncludeWebFetch)
        {
            Add(new RenamedAIFunction(client.AsExtractTool(), FetchName));
        }

        void Add(AIFunction tool) =>
            HarnessTools.AddBuiltIn(context.ChatOptions, context.ToolEffects, tool);
    }

    internal sealed class RenamedAIFunction(AIFunction inner, string name)
        : DelegatingAIFunction(inner)
    {
        public override string Name => name;
    }
}
