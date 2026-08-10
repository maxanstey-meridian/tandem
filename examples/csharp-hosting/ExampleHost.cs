using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;
using Tandem.Ledger;
using Tandem.Terminal;

namespace Tandem.Examples.Hosting;

public sealed record ExampleClients(IChatClient DeepSeek, IChatClient Sol);

public sealed record ExampleRun<TState>(
    Pipeline<TState> Pipeline,
    TState InitialState,
    Func<PipelineRunResult<TState>, string> FormatResult,
    string? LedgerPath = null
);

public static class ExampleHost
{
    public const string DeepSeekModel = "deepseek/deepseek-v4-flash-0731";
    public const string SolModel = "gpt-5.6-sol";
    private static readonly Uri _openRouterEndpoint = new("https://openrouter.ai/api/v1/");
    private static readonly Uri _solEndpoint = new("http://127.0.0.1:10531/v1/");
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
                Console.Error.WriteLine("OPENROUTER_API_KEY is required.");
                return 2;
            }

            await VerifySolAsync(hostCancellation.Token);
            using var deepSeek = CreateCompletionsClient(
                _openRouterEndpoint,
                DeepSeekModel,
                apiKey
            );
            using var sol = CreateResponsesClient(_solEndpoint, SolModel);
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
        IPipelineObserver observer = persistenceObserver is null
            ? display.Observer
            : new PersistentCompositeObserver(persistenceObserver, display.Observer);
        PipelineRunResult<TState>? result = null;
        Exception? executionFailure = null;
        Exception? terminalizationFailure = null;

        await display.StartAsync();
        try
        {
            result = await new PipelineRunner().RunAsync(
                run.Pipeline,
                run.InitialState,
                new PipelineRunOptions(runId, Observer: observer),
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

    private sealed class PersistentCompositeObserver(
        IPipelinePersistenceObserver persistenceObserver,
        IPipelineObserver displayObserver
    ) : IPipelinePersistenceObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            await persistenceObserver.ObserveAsync(observation, cancellationToken);
            await displayObserver.ObserveAsync(observation, cancellationToken);
        }
    }

    private sealed record ModelsResponse(IReadOnlyList<ModelInfo> Data);

    private sealed record ModelInfo([property: JsonPropertyName("id")] string Id);
}
