using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;
using Tandem.Ledger;

namespace Tandem.Examples.Hosting;

public sealed record ExampleClients(IChatClient DeepSeek, IChatClient Sol);

public static class ExampleHost
{
    public const string DeepSeekModel = "deepseek/deepseek-v4-flash-0731";
    public const string SolModel = "gpt-5.6-sol";
    private static readonly Uri _openRouterEndpoint = new("https://openrouter.ai/api/v1/");
    private static readonly Uri _solEndpoint = new("http://127.0.0.1:10531/v1/");
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(10);

    public static async Task<int> RunAsync(
        Func<ExampleClients, CancellationToken, Task<int>> run,
        CancellationToken cancellationToken = default
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            timeout.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.Error.WriteLine("OPENROUTER_API_KEY is required.");
                return 2;
            }

            await VerifySolAsync(timeout.Token);
            using var deepSeek = CreateCompletionsClient(
                _openRouterEndpoint,
                DeepSeekModel,
                apiKey
            );
            using var sol = CreateResponsesClient(_solEndpoint, SolModel);
            return await run(new ExampleClients(deepSeek, sol), timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Console.Error.WriteLine("Run cancelled or timed out.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Run faulted: {exception.Message}");
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    public static int PrintResult<TState>(PipelineRunResult<TState> result, string output)
    {
        Console.WriteLine($"Status: {result.Status}");
        Console.WriteLine(output);
        return result.Status == PipelineRunStatus.Succeeded ? 0 : 1;
    }

    public static async Task<int> RunPersistentAsync<TState>(
        Pipeline<TState> pipeline,
        TState initialState,
        string ledgerPath,
        Func<PipelineRunResult<TState>, string> output,
        CancellationToken cancellationToken
    )
    {
        var runId = Guid.CreateVersion7();
        var fullLedgerPath = Path.GetFullPath(ledgerPath);
        var ledger = new SqliteLedgerStore(fullLedgerPath);
        var observer = await ledger.CreateObserverAsync(runId, pipeline, cancellationToken);
        PipelineRunResult<TState>? result = null;
        Exception? originalFailure = null;
        try
        {
            result = await new PipelineRunner().RunAsync(
                pipeline,
                initialState,
                new PipelineRunOptions(runId, Observer: observer),
                cancellationToken
            );
            return PrintResult(result, output(result));
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            throw;
        }
        finally
        {
            var status = result?.Status switch
            {
                PipelineRunStatus.Succeeded => LedgerRunStatus.Ready,
                PipelineRunStatus.Failed => LedgerRunStatus.Failed,
                _ when cancellationToken.IsCancellationRequested => LedgerRunStatus.Cancelled,
                _ => LedgerRunStatus.Faulted,
            };
            try
            {
                await ledger.CompleteRunAsync(runId, status, CancellationToken.None);
            }
            catch (Exception terminalizationFailure) when (originalFailure is not null)
            {
                Console.Error.WriteLine(
                    $"Warning: ledger terminalization failed: {terminalizationFailure.Message}"
                );
            }
            Console.WriteLine($"Ledger: {fullLedgerPath}");
            Console.WriteLine($"Run: {runId:N}");
        }
    }

    private static IChatClient CreateCompletionsClient(Uri endpoint, string model, string apiKey)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = endpoint }
        );
        return client.GetChatClient(model).AsIChatClient();
    }

#pragma warning disable OPENAI001
    private static IChatClient CreateResponsesClient(Uri endpoint, string model)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential("local-proxy-placeholder"),
            new OpenAIClientOptions { Endpoint = endpoint }
        );
        return client
            .GetResponsesClient()
            .AsIChatClient(model)
            .AsBuilder()
            .ConfigureOptions(options =>
                options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }
            )
            .Build();
    }
#pragma warning restore OPENAI001

    private static async Task VerifySolAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = _solEndpoint };
        var models = await http.GetFromJsonAsync<ModelsResponse>("models", cancellationToken);
        if (
            models?.Data.Any(model => string.Equals(model.Id, SolModel, StringComparison.Ordinal))
            != true
        )
        {
            throw new InvalidOperationException(
                $"{_solEndpoint}models does not expose required model '{SolModel}'."
            );
        }
    }

    private sealed record ModelsResponse(IReadOnlyList<ModelInfo> Data);

    private sealed record ModelInfo([property: JsonPropertyName("id")] string Id);
}
