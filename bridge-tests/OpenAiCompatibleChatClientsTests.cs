using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Tandem.OpenAICompatible;
using Xunit;

namespace Tandem.Bridge;

public sealed class OpenAiCompatibleChatClientsTests
{
#pragma warning disable SCME0001
    [Fact]
    public void ReasoningBudgetConfiguresOpenRouterRequestBody()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new() { ["reasoningMaxTokens"] = 1024 },
        };

        typeof(ReasoningExtractionChatClient)
            .GetMethod("ConfigureReasoningBudget", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [options]);

        var raw = Assert.IsType<ChatCompletionOptions>(options.RawRepresentationFactory!(null!));
        Assert.Equal("1024", raw.Patch.GetJson("$.reasoning.max_tokens"u8).ToString());
    }
#pragma warning restore SCME0001

    [Fact]
    public void OpenRouterCompletionsUseReasoningAdapter()
    {
        const string environmentVariable = "TANDEM_TEST_OPENROUTER_KEY";
        Environment.SetEnvironmentVariable(environmentVariable, "test-key");
        try
        {
            using var client = OpenAiCompatibleChatClients.Create(
                new(
                    RegisteredChatClientKind.OpenAiCompatible,
                    1,
                    "https://openrouter.ai/api/v1",
                    "model",
                    RegisteredWireApi.Completions,
                    environmentVariable
                )
            );

            Assert.IsType<StreamRetryChatClient>(client);
            Assert.IsType<ReasoningExtractionChatClient>(
                client.GetService(typeof(ReasoningExtractionChatClient))
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, null);
        }
    }

    [Fact]
    public async Task OpenRouterStreamingResponsePreservesReasoningAndUsage()
    {
        var handler = new FakeHttpHandler(CompletionStream);
        var openAi = new OpenAIClient(
            new ApiKeyCredential("test-key"),
            new OpenAIClientOptions { Endpoint = new Uri(BaseUrl), Transport = handler.Transport }
        );
        using IChatClient client = new ReasoningExtractionChatClient(
            openAi.GetChatClient("model").AsIChatClient()
        );
        var updates = new List<ChatResponseUpdate>();

        await foreach (
            var update in client.GetStreamingResponseAsync([
                new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Hello"),
            ])
        )
        {
            updates.Add(update);
        }

        Assert.Equal(
            "Think carefully.",
            string.Concat(
                updates
                    .SelectMany(update => update.Contents)
                    .OfType<TextReasoningContent>()
                    .Select(content => content.Text)
            )
        );
        Assert.Equal(
            12,
            Assert
                .Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>())
                .Details.ReasoningTokenCount
        );
        Assert.Equal("/v1/chat/completions", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task OpenRouterStreamingProviderErrorIsReportedAtTheAdapterBoundary()
    {
        var handler = new FakeHttpHandler(ProviderErrorStream);
        var openAi = new OpenAIClient(
            new ApiKeyCredential("test-key"),
            new OpenAIClientOptions { Endpoint = new Uri(BaseUrl), Transport = handler.Transport }
        );
        using IChatClient client = new ReasoningExtractionChatClient(
            openAi.GetChatClient("model").AsIChatClient()
        );

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (
                var _ in client.GetStreamingResponseAsync([
                    new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Hello"),
                ])
            ) { }
        });

        Assert.Equal(
            "OpenRouter terminated the streaming response with a provider error.",
            exception.Message
        );
        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
        Assert.Equal("/v1/chat/completions", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task ModelPreflightRequiresExactModelExposure()
    {
        var handler = new FakeHttpHandler(ModelList("other-model"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiCompatibleChatClients.VerifyModelAsync(
                Client(BaseUrl),
                CancellationToken.None,
                handler.Transport
            )
        );

        Assert.Contains("does not expose required model 'required-model'", exception.Message);
        Assert.Equal("/v1/models", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task ModelPreflightAcceptsExposedModelWithoutASecretForLoopback()
    {
        var handler = new FakeHttpHandler(ModelList("required-model"));

        await OpenAiCompatibleChatClients.VerifyModelAsync(
            Client(BaseUrl),
            CancellationToken.None,
            handler.Transport
        );

        Assert.Equal("/v1/models", Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task ModelPreflightObservesCancellation()
    {
        var handler = new FakeHttpHandler(
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new UnreachableException();
            }
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiCompatibleChatClients.VerifyModelAsync(
                Client(BaseUrl),
                cancellation.Token,
                handler.Transport
            )
        );
    }

    private const string BaseUrl = "http://127.0.0.1:1/v1";

    private static RegisteredChatClientContract Client(string endpoint) =>
        new(
            RegisteredChatClientKind.OpenAiCompatible,
            1,
            endpoint,
            "required-model",
            RegisteredWireApi.Responses,
            VerifyModel: true
        );

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ModelList(
        string model
    ) =>
        (_, _) =>
            Task.FromResult(
                Response(
                    "application/json",
                    JsonSerializer.Serialize(new { data = new[] { new { id = model } } })
                )
            );

    private static Task<HttpResponseMessage> CompletionStream(
        HttpRequestMessage request,
        CancellationToken token
    ) =>
        Task.FromResult(
            Response(
                "text/event-stream",
                "data: {\"id\":\"completion\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"Think carefully.\"},\"finish_reason\":null}]}\n\n"
                    + "data: {\"id\":\"completion\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":15,\"total_tokens\":20,\"completion_tokens_details\":{\"reasoning_tokens\":12}}}\n\n"
                    + "data: [DONE]\n\n"
            )
        );

    private static Task<HttpResponseMessage> ProviderErrorStream(
        HttpRequestMessage request,
        CancellationToken token
    ) =>
        Task.FromResult(
            Response(
                "text/event-stream",
                "data: {\"id\":\"completion\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"provider\":\"Provider\",\"error\":{\"code\":429,\"message\":\"Rate limit exceeded\",\"metadata\":{\"error_type\":\"rate_limit_exceeded\"}},\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"error\"}]}\n\n"
            )
        );

    private static HttpResponseMessage Response(string contentType, string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private sealed class FakeHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond
    ) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public HttpClientPipelineTransport Transport => new(new HttpClient(this));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return respond(request, cancellationToken);
        }
    }
}
