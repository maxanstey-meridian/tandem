using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Tandem.Domain;
using Tandem.OpenAICompatible;

namespace Tandem.Infrastructure;

public sealed class ChatClientBuilder
{
    public IChatClient Build(ResolvedProfile profile, string apiKey)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(profile.BaseUrl) };

        return profile.WireApi switch
        {
            Domain.WireApi.Completions => BuildCompletions(profile, apiKey, options),
            Domain.WireApi.Responses => BuildResponses(profile, apiKey, options),
            _ => throw new ArgumentException($"Unsupported wire API: {profile.WireApi}"),
        };
    }

    private static IChatClient BuildCompletions(
        ResolvedProfile profile,
        string apiKey,
        OpenAIClientOptions options
    )
    {
        var client = CreateOpenAIClient(apiKey, options);
        var chatClient = client.GetChatClient(profile.Model).AsIChatClient();
        var configured = ApplyReasoning(chatClient, profile.Reasoning);
        return profile.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            ? new OpenRouterReasoningChatClient(configured)
            : configured;
    }

#pragma warning disable OPENAI001
    private static IChatClient BuildResponses(
        ResolvedProfile profile,
        string apiKey,
        OpenAIClientOptions options
    )
    {
        var client = CreateOpenAIClient(apiKey, options);
        var chatClient = client.GetResponsesClient().AsIChatClient(profile.Model);
        return ApplyReasoning(chatClient, profile.Reasoning);
    }
#pragma warning restore OPENAI001

    private static OpenAIClient CreateOpenAIClient(string apiKey, OpenAIClientOptions options)
    {
        return string.IsNullOrEmpty(apiKey)
            ? new OpenAIClient(new ApiKeyCredential("tandem-local-proxy-placeholder"), options)
            : new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    private static IChatClient ApplyReasoning(IChatClient chatClient, ReasoningLevel? reasoning)
    {
        if (reasoning is null)
        {
            return chatClient;
        }

        var effort = reasoning.Value switch
        {
            Domain.ReasoningLevel.Low => Microsoft.Extensions.AI.ReasoningEffort.Low,
            Domain.ReasoningLevel.Medium => Microsoft.Extensions.AI.ReasoningEffort.Medium,
            Domain.ReasoningLevel.High => Microsoft.Extensions.AI.ReasoningEffort.High,
            _ => throw new ArgumentException($"Unsupported reasoning level: {reasoning.Value}"),
        };

        return chatClient
            .AsBuilder()
            .ConfigureOptions(o => o.Reasoning = new ReasoningOptions { Effort = effort })
            .Build();
    }
}
