using System.ClientModel;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Tandem.OpenAICompatible;

namespace Tandem.NodeApiSpike;

internal static class OpenAiCompatibleChatClients
{
    public static IChatClient Create(
        RegisteredChatClientContract descriptor,
        int? reasoningMaxTokens = null
    )
    {
        var endpoint = new Uri(descriptor.Endpoint, UriKind.Absolute);
        var apiKey = ApiKey(descriptor);
        var clientOptions = new OpenAIClientOptions { Endpoint = endpoint };
        if (descriptor.MaxAttempts is not null)
        {
            // The outer transport wrapper owns the explicit attempt budget.
            clientOptions.RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0);
        }
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        IChatClient chatClient;
        if (descriptor.WireApi == RegisteredWireApi.Responses)
        {
#pragma warning disable OPENAI001
            chatClient = client.GetResponsesClient().AsIChatClient(descriptor.Model);
#pragma warning restore OPENAI001
        }
        else
        {
            chatClient = client.GetChatClient(descriptor.Model).AsIChatClient();
        }

        if (
            descriptor.WireApi == RegisteredWireApi.Completions
            && (
                reasoningMaxTokens is not null
                || endpoint.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
                || endpoint.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            chatClient = new ReasoningExtractionChatClient(chatClient);
        }

        if (descriptor.RequestTimeoutMs is not null || descriptor.IdleTimeoutMs is not null)
        {
            chatClient = new RequestDeadlineChatClient(
                chatClient,
                descriptor.RequestTimeoutMs is { } total ? TimeSpan.FromMilliseconds(total) : null,
                descriptor.IdleTimeoutMs is { } idle ? TimeSpan.FromMilliseconds(idle) : null
            );
        }
        return new StreamRetryChatClient(chatClient, maxAttempts: descriptor.MaxAttempts ?? 4);
    }

    public static async Task VerifyModelAsync(
        RegisteredChatClientContract descriptor,
        CancellationToken cancellationToken
    )
    {
        var endpoint = new Uri(descriptor.Endpoint, UriKind.Absolute);
        var model = descriptor.Model;
        var apiKey = ApiKey(descriptor);
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

    private static string ApiKey(RegisteredChatClientContract descriptor) =>
        descriptor.ApiKeyEnvironmentVariable is null
            ? "tandem-local-proxy-placeholder"
            : Environment.GetEnvironmentVariable(descriptor.ApiKeyEnvironmentVariable)
                ?? throw new InvalidOperationException(
                    $"Chat client API key environment variable '{descriptor.ApiKeyEnvironmentVariable}' is not set."
                );

    private sealed record ModelList(Model[] Data);

    private sealed record Model(string Id);
}
