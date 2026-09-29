using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using Tandem.OpenAICompatible;

namespace Tandem.Bridge;

internal static class OpenAiCompatibleChatClients
{
    public static IChatClient Create(
        RegisteredChatClientContract descriptor,
        int? reasoningMaxTokens = null
    )
    {
        var endpoint = new Uri(descriptor.Endpoint, UriKind.Absolute);
        var client = OpenAiClient(descriptor, transport: null);
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
        CancellationToken cancellationToken,
        PipelineTransport? transport = null
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var models = await OpenAiClient(descriptor, transport)
            .GetOpenAIModelClient()
            .GetModelsAsync(timeout.Token);
        if (!models.Value.Any(model => model.Id == descriptor.Model))
        {
            throw new InvalidOperationException(
                $"Chat client endpoint '{descriptor.Endpoint}' does not expose required model '{descriptor.Model}'."
            );
        }
    }

    private static OpenAIClient OpenAiClient(
        RegisteredChatClientContract descriptor,
        PipelineTransport? transport
    )
    {
        // StreamRetryChatClient is the only retry layer; the SDK's default retries would multiply it.
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(descriptor.Endpoint, UriKind.Absolute),
            RetryPolicy = new ClientRetryPolicy(0),
        };
        if (transport is not null)
        {
            options.Transport = transport;
        }
        return new OpenAIClient(new ApiKeyCredential(ApiKey(descriptor)), options);
    }

    private static string ApiKey(RegisteredChatClientContract descriptor) =>
        descriptor.ApiKeyEnvironmentVariable is null
            ? "tandem-local-proxy-placeholder"
            : Environment.GetEnvironmentVariable(descriptor.ApiKeyEnvironmentVariable)
                ?? throw new InvalidOperationException(
                    $"Chat client API key environment variable '{descriptor.ApiKeyEnvironmentVariable}' is not set."
                );
}
