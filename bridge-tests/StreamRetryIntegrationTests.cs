using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class StreamRetryIntegrationTests
{
    [Theory]
    [InlineData("completions")]
    [InlineData("responses")]
    public async Task FactoryRetriesDroppedStreamsWithoutLeakingPartialOutput(string wireApi)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var requests = new List<string>();
        var serve = ServeAsync(listener, requests, wireApi, cancellation.Token);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = await OpenAiCompatibleChatClients.CreateAsync(
            new(
                "openai-compatible",
                1,
                $"http://127.0.0.1:{port}/v1",
                "model",
                wireApi,
                null,
                false
            ),
            cancellation.Token
        );
        var text = new StringBuilder();
        await foreach (
            var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Keep this exact request")],
                cancellationToken: cancellation.Token
            )
        )
        {
            text.Append(update.Text);
        }
        await serve;
        Assert.Equal("recovered", text.ToString());
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0], requests[1]);
    }

    private static async Task ServeAsync(
        TcpListener listener,
        List<string> requests,
        string wireApi,
        CancellationToken token
    )
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var connection = await listener.AcceptTcpClientAsync(token);
            await using var stream = connection.GetStream();
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, token) == 0)
                {
                    throw new IOException("Missing request");
                }
                bytes.Add(one[0]);
                if (
                    bytes.Count >= 4
                    && Encoding.ASCII.GetString(bytes.ToArray()[^4..]) == "\r\n\r\n"
                )
                {
                    break;
                }
            }
            var headers = Encoding.ASCII.GetString(bytes.ToArray());
            var length = int.Parse(
                headers
                    .Split("\r\n")
                    .Single(l =>
                        l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    )
                    .Split(':')[1]
                    .Trim()
            );
            var requestBody = new byte[length];
            await stream.ReadExactlyAsync(requestBody, token);
            requests.Add(Encoding.UTF8.GetString(requestBody));
            var body = wireApi == "responses" ? Responses(attempt == 0) : Completions(attempt == 0);
            var encoded = Encoding.UTF8.GetBytes(body);
            // The first connection ends while a declared response body is still incomplete.
            var announced = encoded.Length + (attempt == 0 ? 1000 : 0);
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {announced}\r\nConnection: close\r\n\r\n"
                ),
                token
            );
            await stream.WriteAsync(encoded, token);
            await stream.FlushAsync(token);
        }
    }

    private static string Completions(bool drop) =>
        "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\""
        + (drop ? "discard me" : "recovered")
        + "\"},\"finish_reason\":null}]}\n\n"
        + (
            drop
                ? ""
                : "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
        );

    private static string Responses(bool drop)
    {
        var content = drop ? "discard me" : "recovered";
        var response = new
        {
            id = "resp_test",
            @object = "response",
            created_at = 1,
            status = "completed",
            model = "model",
            output = new[]
            {
                new
                {
                    id = "msg_test",
                    type = "message",
                    role = "assistant",
                    status = "completed",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = content,
                            annotations = Array.Empty<object>(),
                        },
                    },
                },
            },
            usage = new
            {
                input_tokens = 1,
                output_tokens = 1,
                total_tokens = 2,
            },
        };
        string Event(string type, object data) =>
            $"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n";
        return Event("response.created", new { type = "response.created", response })
            + Event(
                "response.output_text.delta",
                new
                {
                    type = "response.output_text.delta",
                    item_id = "msg_test",
                    output_index = 0,
                    content_index = 0,
                    delta = content,
                }
            )
            + (
                drop
                    ? ""
                    : Event("response.completed", new { type = "response.completed", response })
            );
    }
}
