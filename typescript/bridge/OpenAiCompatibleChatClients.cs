using System.ClientModel;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Tandem.OpenAICompatible;

namespace Tandem.NodeApiSpike;

internal static class OpenAiCompatibleChatClients
{
    public static async Task<IChatClient> CreateAsync(
        RegisteredChatClientContract descriptor,
        CancellationToken cancellationToken
    )
    {
        var endpoint = new Uri(descriptor.Endpoint!, UriKind.Absolute);
        var apiKey = descriptor.ApiKeyEnvironmentVariable is null
            ? "tandem-local-proxy-placeholder"
            : Environment.GetEnvironmentVariable(descriptor.ApiKeyEnvironmentVariable)
                ?? throw new InvalidOperationException(
                    $"Chat client API key environment variable '{descriptor.ApiKeyEnvironmentVariable}' is not set."
                );

        if (descriptor.VerifyModel)
        {
            await VerifyModelAsync(endpoint, descriptor.Model!, apiKey, cancellationToken);
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = endpoint }
        );
        IChatClient chatClient;
        if (descriptor.WireApi == "responses")
        {
#pragma warning disable OPENAI001
            chatClient = client.GetResponsesClient().AsIChatClient(descriptor.Model!);
#pragma warning restore OPENAI001
        }
        else
        {
            chatClient = client.GetChatClient(descriptor.Model!).AsIChatClient();
        }

        if (
            descriptor.WireApi == "completions"
            && (
                endpoint.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
                || endpoint.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            chatClient = new OpenRouterReasoningChatClient(chatClient);
        }

        return chatClient;
    }

    private static async Task VerifyModelAsync(
        Uri endpoint,
        string model,
        string apiKey,
        CancellationToken cancellationToken
    )
    {
        using var http = new HttpClient
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(5),
        };
        if (apiKey != "tandem-local-proxy-placeholder")
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        }
        using var response = await http.GetAsync(
            $"{endpoint.AbsolutePath.TrimEnd('/')}/models",
            cancellationToken
        );
        response.EnsureSuccessStatusCode();
        var models = await response.Content.ReadFromJsonAsync<ModelList>(cancellationToken);
        if (models?.Data.Any(item => item.Id == model) != true)
        {
            throw new InvalidOperationException(
                $"Chat client endpoint '{endpoint}' does not expose required model '{model}'."
            );
        }
    }

    private sealed record ModelList(Model[] Data);

    private sealed record Model(string Id);
}
