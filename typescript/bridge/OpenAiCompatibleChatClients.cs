using System.ClientModel;
using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Tandem.NodeApiSpike;

internal static class OpenAiCompatibleChatClients
{
    public static async Task<IChatClient> CreateAsync(RegisteredChatClientContract descriptor)
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
            await VerifyModelAsync(endpoint, descriptor.Model!, apiKey);
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

        if (descriptor.ReasoningEffort is { } effort)
        {
            chatClient = chatClient
                .AsBuilder()
                .ConfigureOptions(options =>
                    options.Reasoning = new ReasoningOptions
                    {
                        Effort = effort switch
                        {
                            "low" => ReasoningEffort.Low,
                            "medium" => ReasoningEffort.Medium,
                            "high" => ReasoningEffort.High,
                            _ => throw new UnreachableException(),
                        },
                    }
                )
                .Build();
        }
        return chatClient;
    }

    private static async Task VerifyModelAsync(Uri endpoint, string model, string apiKey)
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
        using var response = await http.GetAsync($"{endpoint.AbsolutePath.TrimEnd('/')}/models");
        response.EnsureSuccessStatusCode();
        var models = await response.Content.ReadFromJsonAsync<ModelList>();
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
