using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;
using Tandem.Ledger;
using Tandem.OpenAICompatible;
using Tandem.Terminal;

namespace Tandem.Examples.Hosting;

public sealed record ExampleClients(IChatClient DeepSeek, IChatClient Sol);

public sealed record ExampleRun<TState>(
    Pipeline<TState> Pipeline,
    TState InitialState,
    Func<PipelineRunResult<TState>, string> FormatResult,
    string? LedgerPath = null
);

/// <summary>
/// Where the second model role runs: OpenRouter by default, or an OpenAI-compatible Responses
/// endpoint such as a local openai-oauth proxy when <c>TANDEM_EXAMPLE_LOCAL_BASE_URL</c> is set.
/// </summary>
internal sealed record ExampleLocalModel(Uri? LocalEndpoint, string Model);

public static class ExampleHost
{
    public const string DeepSeekModel = "deepseek/deepseek-v4-flash-0731";
    internal const string LocalBaseUrlVariable = "TANDEM_EXAMPLE_LOCAL_BASE_URL";
    internal const string LocalModelVariable = "TANDEM_EXAMPLE_LOCAL_MODEL";
    internal const string DefaultLocalModel = "gpt-5.6-sol";
    internal const string DefaultOpenRouterLocalModel = "openai/gpt-5.6-sol";
    private static readonly Uri _openRouterEndpoint = new("https://openrouter.ai/api/v1/");
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(10);

    public static async Task<int> RunAsync<TState>(
        Func<ExampleClients, ExampleRun<TState>> createRun,
        CancellationToken cancellationToken = default
    )
    {
        using var hostCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        hostCancellation.CancelAfter(_timeout);
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            hostCancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.Error.WriteLine(
                    "OPENROUTER_API_KEY is required to run the examples. See \"Run the examples\" in README.md."
                );
                return 2;
            }

            var localModel = ResolveLocalModel(
                Environment.GetEnvironmentVariable(LocalBaseUrlVariable),
                Environment.GetEnvironmentVariable(LocalModelVariable)
            );
            if (localModel is null)
            {
                Console.Error.WriteLine(
                    $"{LocalBaseUrlVariable} must be an absolute http(s) URL, such as http://127.0.0.1:10531/v1."
                );
                return 2;
            }

            if (localModel.LocalEndpoint is { } localEndpoint)
            {
                try
                {
                    await VerifyLocalModelAsync(
                        localEndpoint,
                        localModel.Model,
                        hostCancellation.Token
                    );
                }
                catch (HttpRequestException exception)
                    when (exception.HttpRequestError == HttpRequestError.ConnectionError)
                {
                    Console.Error.WriteLine(
                        $"Cannot reach {LocalBaseUrlVariable}={localEndpoint} ({exception.Message}). "
                            + "Start the openai-oauth proxy (`npx openai-oauth login`, then `npx openai-oauth`) "
                            + $"or unset {LocalBaseUrlVariable} to use OpenRouter."
                    );
                    return 2;
                }
            }

            using var deepSeek = CreateCompletionsClient(
                _openRouterEndpoint,
                DeepSeekModel,
                apiKey
            );
            using var sol = localModel.LocalEndpoint is { } endpoint
                ? CreateResponsesClient(endpoint, localModel.Model)
                : WithLowReasoning(
                    CreateCompletionsClient(_openRouterEndpoint, localModel.Model, apiKey)
                );
            return await RunPipelineAsync(
                createRun(new ExampleClients(deepSeek, sol)),
                TerminalCapabilities.Detect(),
                console: null,
                keyInput: null,
                Console.Out,
                Console.Error,
                hostCancellation.Token
            );
        }
        catch (OperationCanceledException) when (hostCancellation.IsCancellationRequested)
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

    /// <summary>
    /// Resolves the second model role from the example environment, or returns <see langword="null"/>
    /// when the configured local base URL is not an absolute http(s) URL.
    /// </summary>
    internal static ExampleLocalModel? ResolveLocalModel(string? localBaseUrl, string? localModel)
    {
        var model = string.IsNullOrWhiteSpace(localModel) ? null : localModel.Trim();
        if (string.IsNullOrWhiteSpace(localBaseUrl))
        {
            return new ExampleLocalModel(null, model ?? DefaultOpenRouterLocalModel);
        }

        var trimmed = localBaseUrl.Trim();
        // A base URL without a trailing slash would drop its last segment when "models" is resolved.
        if (
            !Uri.TryCreate(
                trimmed.EndsWith('/') ? trimmed : trimmed + "/",
                UriKind.Absolute,
                out var endpoint
            ) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
        )
        {
            return null;
        }

        return new ExampleLocalModel(endpoint, model ?? DefaultLocalModel);
    }

    internal static async Task<int> RunPipelineAsync<TState>(
        ExampleRun<TState> run,
        TerminalCapabilities capabilities,
        Spectre.Console.IAnsiConsole? console,
        ITerminalKeyInput? keyInput,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(run);
        var runId = Guid.CreateVersion7();
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        SqliteLedgerStore? ledger = null;
        SqlitePipelineObserver? persistenceObserver = null;
        string? ledgerPath = null;
        if (run.LedgerPath is not null)
        {
            ledgerPath = Path.GetFullPath(run.LedgerPath);
            ledger = new SqliteLedgerStore(ledgerPath);
            persistenceObserver = await ledger.CreateObserverAsync(
                runId,
                run.Pipeline,
                cancellationToken
            );
        }

        await using var display = new TerminalPipelineDisplay(
            run.Pipeline.Inspect(),
            runId,
            new TerminalDisplayOptions
            {
                Console = console,
                Capabilities = capabilities,
                KeyInput = keyInput,
                CancelAsync = _ =>
                {
                    runCancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
            }
        );
        PipelineRunResult<TState>? result = null;
        Exception? executionFailure = null;
        Exception? terminalizationFailure = null;

        await display.StartAsync();
        try
        {
            result = await new PipelineRunner().RunAsync(
                run.Pipeline,
                run.InitialState,
                new PipelineRunOptions(
                    runId,
                    Observer: PipelineObservers.Compose(persistenceObserver, display.Observer)
                ),
                runCancellation.Token
            );
        }
        catch (Exception exception)
        {
            executionFailure = exception;
        }

        if (ledger is not null)
        {
            var status = result?.Status switch
            {
                PipelineRunStatus.Succeeded => LedgerRunStatus.Ready,
                PipelineRunStatus.Failed => LedgerRunStatus.Failed,
                _ when runCancellation.IsCancellationRequested => LedgerRunStatus.Cancelled,
                _ => LedgerRunStatus.Faulted,
            };
            try
            {
                await ledger.CompleteRunAsync(runId, status, CancellationToken.None);
            }
            catch (Exception exception)
            {
                terminalizationFailure = exception;
            }
        }

        var failure = executionFailure ?? terminalizationFailure;
        if (result?.Status == PipelineRunStatus.Succeeded && failure is null)
        {
            await display.SucceededAsync(result.Outcome?.Summary ?? "Pipeline succeeded");
        }
        else if (result?.Status == PipelineRunStatus.Failed && failure is null)
        {
            await display.FailedAsync(result.Outcome?.Summary ?? "Pipeline failed");
        }
        else if (runCancellation.IsCancellationRequested)
        {
            await display.CancelledAsync("Run cancelled or timed out");
        }
        else
        {
            await display.FaultedAsync(failure?.Message ?? "Pipeline faulted");
        }
        await display.WaitForCleanupAsync();

        if (terminalizationFailure is not null && executionFailure is not null)
        {
            await error.WriteLineAsync(
                $"Warning: ledger terminalization failed: {terminalizationFailure.Message}"
            );
        }
        if (result is not null)
        {
            await output.WriteLineAsync($"Status: {result.Status}");
            await output.WriteLineAsync(run.FormatResult(result));
        }
        if (ledgerPath is not null)
        {
            await output.WriteLineAsync($"Ledger: {ledgerPath}");
            await output.WriteLineAsync($"Run: {runId:N}");
        }
        if (failure is not null)
        {
            var message = runCancellation.IsCancellationRequested
                ? "Run cancelled or timed out."
                : $"Run faulted: {failure.Message}";
            await error.WriteLineAsync(message);
            return 2;
        }
        return result?.Status == PipelineRunStatus.Succeeded ? 0 : 1;
    }

    internal static IChatClient CreateCompletionsClient(Uri endpoint, string model, string apiKey)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = endpoint }
        );
        return new ReasoningExtractionChatClient(client.GetChatClient(model).AsIChatClient());
    }

#pragma warning disable OPENAI001
    private static IChatClient CreateResponsesClient(Uri endpoint, string model)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential("local-proxy-placeholder"),
            new OpenAIClientOptions { Endpoint = endpoint }
        );
        return WithLowReasoning(client.GetResponsesClient().AsIChatClient(model));
    }
#pragma warning restore OPENAI001

    private static IChatClient WithLowReasoning(IChatClient client) =>
        client
            .AsBuilder()
            .ConfigureOptions(options =>
                options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }
            )
            .Build();

    private static async Task VerifyLocalModelAsync(
        Uri endpoint,
        string model,
        CancellationToken cancellationToken
    )
    {
        using var http = new HttpClient { BaseAddress = endpoint };
        var models = await http.GetFromJsonAsync<ModelsResponse>("models", cancellationToken);
        if (
            models?.Data.Any(candidate =>
                string.Equals(candidate.Id, model, StringComparison.Ordinal)
            ) != true
        )
        {
            throw new InvalidOperationException(
                $"{endpoint}models does not expose required model '{model}'. Set {LocalModelVariable} to a model it serves."
            );
        }
    }

    private sealed record ModelsResponse(IReadOnlyList<ModelInfo> Data);

    private sealed record ModelInfo([property: JsonPropertyName("id")] string Id);
}
