using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class RawOutputIntegrationTests
{
    [Theory]
    [InlineData("review-decision")]
    [InlineData("implementation-report")]
    public async Task Raw_output_corrects_contextual_problems_and_preserves_ledger_identity(
        string valueType
    )
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = new List<string>();
        var serve = ServeAsync(listener, requests, cancellation.Token);
        var path = Path.Combine(Path.GetTempPath(), $"tandem-raw-{Guid.NewGuid():N}.sqlite3");
        var validations = 0;
        var applications = 0;
        string Callback(string callback, string state, string input)
        {
            var value = callback switch
            {
                "message" => "Decide.",
                "parse" => JsonSerializer.Serialize(new { answer = input }),
                "validate" => ++validations == 1
                    ? "[{\"path\":\"$.answer\",\"message\":\"Confirm the answer\"},{\"path\":\"$.reason\",\"message\":\"Confirm the reason\"}]"
                    : "[]",
                "apply" => Apply(input),
                "summary" => "Done.",
                _ => throw new InvalidOperationException(callback),
            };
            return JsonSerializer.Serialize(new { succeeded = true, value });
        }
        string Apply(string input)
        {
            applications++;
            return input;
        }
        var definition = $$$"""
            {"contractVersion":10,"name":"raw-proof","start":"review","initialState":"{}","persist":true,
             "ledgerPath":{{{JsonSerializer.Serialize(path)}}},
             "nodes":[
               {"id":"review","kind":"agent","instructions":"Review.","messageCallback":"message","capabilities":[],"skillDirectories":[],
                "client":{"kind":"openai-compatible","version":1,"endpoint":"http://127.0.0.1:{{{port}}}/v1","model":"model","wireApi":"completions","verifyModel":false},
                "output":{"raw":true,"rawParseCallback":"parse","validateForCallback":"validate","applyCallback":"apply","instructions":"Return a plain word.","valueType":"{{{valueType}}}"}},
               {"id":"done","kind":"completion","summaryCallback":"summary"}],
             "routes":[{"source":"review","target":"done","outcome":"success","label":"accepted"}],"outputs":["done"]}
            """;
        try
        {
            var json = await NodePipelineBridge.RunRegisteredGraphAsync(
                definition,
                Callback,
                (id, state, input, _) => Task.FromResult(Callback(id, state, input)),
                cancellation.Token
            );
            await serve;
            using var result = JsonDocument.Parse(json);
            Assert.True(result.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal(1, applications);
            Assert.Equal(2, validations);
            var acceptedJson = await NodePipelineBridge.InspectAcceptedAsync(
                path,
                result.RootElement.GetProperty("runId").GetString()!
            );
            using var accepted = JsonDocument.Parse(acceptedJson);
            Assert.Contains(
                accepted.RootElement.EnumerateArray(),
                item =>
                    item.GetProperty("valueType").GetString() == valueType
                    && item.GetProperty("payload").GetProperty("answer").GetString() == "accepted"
            );
            Assert.Contains("Return a plain word.", requests[0]);
            Assert.Contains("Confirm the answer", requests[1]);
            Assert.Contains("Confirm the reason", requests[1]);
            Assert.DoesNotContain("corrected JSON object", requests[1]);
            Assert.All(requests, request => Assert.DoesNotContain("response_format", request));
        }
        finally
        {
            listener.Stop();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    private static async Task ServeAsync(
        TcpListener listener,
        List<string> requests,
        CancellationToken token
    )
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var connection = await listener.AcceptTcpClientAsync(token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var length = 0;
            while (await reader.ReadLineAsync(token) is { Length: > 0 } header)
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(header.Split(':')[1]);
                }
            }
            var body = new char[length];
            Assert.Equal(length, await reader.ReadBlockAsync(body, token));
            requests.Add(new string(body));
            var payload =
                "data: {\"id\":\"reply\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"accepted\"},\"finish_reason\":null}]}\n\n"
                + "data: {\"id\":\"reply\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"
                ),
                token
            );
            await stream.WriteAsync(bytes, token);
        }
    }
}
