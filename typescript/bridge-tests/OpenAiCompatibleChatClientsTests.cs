using System.ClientModel;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Tandem.OpenAICompatible;
using Xunit;

namespace Tandem.NodeApiSpike;

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

        typeof(OpenRouterReasoningChatClient)
            .GetMethod("ConfigureReasoningBudget", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [options]);

        var raw = Assert.IsType<ChatCompletionOptions>(options.RawRepresentationFactory!(null!));
        Assert.Equal("1024", raw.Patch.GetJson("$.reasoning.max_tokens"u8).ToString());
    }
#pragma warning restore SCME0001

    [Fact]
    public async Task OpenRouterCompletionsUseReasoningAdapter()
    {
        const string environmentVariable = "TANDEM_TEST_OPENROUTER_KEY";
        Environment.SetEnvironmentVariable(environmentVariable, "test-key");
        try
        {
            using var client = await OpenAiCompatibleChatClients.CreateAsync(
                new(
                    "openai-compatible",
                    1,
                    "https://openrouter.ai/api/v1",
                    "model",
                    "completions",
                    environmentVariable,
                    false
                ),
                CancellationToken.None
            );

            Assert.IsType<OpenRouterReasoningChatClient>(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, null);
        }
    }

    [Fact]
    public async Task OpenRouterStreamingResponsePreservesReasoningAndUsage()
    {
        using var server = new CompletionServer();
        var openAi = new OpenAIClient(
            new ApiKeyCredential("test-key"),
            new OpenAIClientOptions { Endpoint = new Uri(server.BaseUrl) }
        );
        using IChatClient client = new OpenRouterReasoningChatClient(
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
        await server.Completion;
    }

    [Fact]
    public async Task OpenRouterStreamingProviderErrorIsReportedAtTheAdapterBoundary()
    {
        using var server = new CompletionServer(providerError: true);
        var openAi = new OpenAIClient(
            new ApiKeyCredential("test-key"),
            new OpenAIClientOptions { Endpoint = new Uri(server.BaseUrl) }
        );
        using IChatClient client = new OpenRouterReasoningChatClient(
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
        await server.Completion;
    }

    [Fact]
    public async Task ModelPreflightRequiresExactModelExposure()
    {
        using var server = new ModelServer("other-model");
        var descriptor = Client(server.BaseUrl) with { VerifyModel = true };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiCompatibleChatClients.CreateAsync(descriptor, CancellationToken.None)
        );

        Assert.Contains("does not expose required model 'required-model'", exception.Message);
        await server.Completion;
    }

    [Fact]
    public async Task ModelPreflightBuildsClientWithoutASecretForLoopback()
    {
        using var server = new ModelServer("required-model");

        using var client = await OpenAiCompatibleChatClients.CreateAsync(
            Client(server.BaseUrl) with
            {
                VerifyModel = true,
            },
            CancellationToken.None
        );

        Assert.NotNull(client);
        await server.Completion;
    }

    [Fact]
    public async Task ModelPreflightObservesCancellation()
    {
        using var server = new ModelServer("required-model", respond: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OpenAiCompatibleChatClients.CreateAsync(
                Client(server.BaseUrl) with
                {
                    VerifyModel = true,
                },
                cancellation.Token
            )
        );
    }

    private static RegisteredChatClientContract Client(string endpoint) =>
        new("openai-compatible", 1, endpoint, "required-model", "responses", null, false);

    private sealed class ModelServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public ModelServer(string model, bool respond = true)
        {
            var port = Random.Shared.Next(20000, 50000);
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Completion = ServeAsync(model, respond);
        }

        public string BaseUrl { get; }
        public Task Completion { get; }

        private async Task ServeAsync(string model, bool respond)
        {
            var context = await _listener.GetContextAsync();
            Assert.Equal("/v1/models", context.Request.Url?.AbsolutePath);
            if (!respond)
            {
                return;
            }
            var body = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { data = new[] { new { id = model } } })
            );
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        public void Dispose() => _listener.Close();
    }

    private sealed class CompletionServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public CompletionServer(bool providerError = false)
        {
            var port = Random.Shared.Next(20000, 50000);
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Completion = ServeAsync(providerError);
        }

        public string BaseUrl { get; }
        public Task Completion { get; }

        private async Task ServeAsync(bool providerError)
        {
            var context = await _listener.GetContextAsync();
            Assert.Equal("/v1/chat/completions", context.Request.Url?.AbsolutePath);
            const string successResponse =
                "data: {\"id\":\"completion\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"Think carefully.\"},\"finish_reason\":null}]}\n\n"
                + "data: {\"id\":\"completion\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":15,\"total_tokens\":20,\"completion_tokens_details\":{\"reasoning_tokens\":12}}}\n\n"
                + "data: [DONE]\n\n";
            const string errorResponse =
                "data: {\"id\":\"completion\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"provider\":\"Provider\",\"error\":{\"code\":429,\"message\":\"Rate limit exceeded\",\"metadata\":{\"error_type\":\"rate_limit_exceeded\"}},\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"error\"}]}\n\n";
            var response = providerError ? errorResponse : successResponse;
            var body = Encoding.UTF8.GetBytes(response);
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }

        public void Dispose() => _listener.Close();
    }
}
