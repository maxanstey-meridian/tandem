using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class OpenAiCompatibleChatClientsTests
{
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
        new("openai-compatible", 1, endpoint, "required-model", "responses", null, "low", false);

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
}
